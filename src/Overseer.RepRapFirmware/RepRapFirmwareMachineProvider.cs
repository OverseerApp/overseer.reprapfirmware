using System.Diagnostics;
using System.Text.Json;
using Overseer.RepRapFirmware.Models;
using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

public class RepRapFirmwareMachineProvider(IHttpClientFactory httpClientFactory)
  : RepRapFirmwareMachineProviderBase<RepRapFirmwareMachine>,
    IDisposable
{
  static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

  System.Timers.Timer? _timer;
  CancellationTokenSource? _cancellation;
  int _exceptionCount;
  readonly Stopwatch _stopwatch = new();
  readonly SemaphoreSlim _pollLock = new(1, 1);
  ConnectResponse? _cachedSession;
  bool _disposed;

  const int MaxExceptionCount = 5;
  const int ExceptionBackoffMinutes = 2;

  public override void Start<TMachine>(int interval, TMachine machine)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);

    Machine = machine as RepRapFirmwareMachine ?? new RepRapFirmwareMachine(machine);

    _cachedSession = null;

    _timer?.Dispose();
    _timer = new(interval) { AutoReset = false };
    _timer.Elapsed += async (sender, args) =>
    {
      try
      {
        await Poll();
      }
      catch (Exception ex)
      {
        Log.Error("Unhandled exception in poll timer callback", ex);
      }
      finally
      {
        try
        {
          _timer?.Start();
        }
        catch (ObjectDisposedException)
        {
          /* timer was disposed during poll */
        }
      }
    };
    _timer.Start();
  }

  public override void Stop()
  {
    _timer?.Dispose();
    _timer = null;
    _cancellation?.Cancel();
    _cancellation?.Dispose();
    _cancellation = null;
    _cachedSession = null;
  }

  public void Dispose()
  {
    if (_disposed)
      return;
    _disposed = true;

    Stop();
    _pollLock.Dispose();

    GC.SuppressFinalize(this);
  }

  async Task Poll()
  {
    if (Machine is null)
      return;

    if (_disposed)
      return;

    if (!await _pollLock.WaitAsync(0))
      return;

    try
    {
      // During backoff, emit an offline status but still attempt to acquire
      // (matches original behavior — allows immediate recovery when the machine comes back)
      if (_stopwatch.IsRunning && _stopwatch.Elapsed.TotalMinutes < ExceptionBackoffMinutes)
      {
        OnStatusUpdated(new MachineStatus { MachineId = Machine.Id });
      }

      try
      {
        // Cancel any in-flight requests from a previous cycle that may have stalled
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        var status = await AcquireStatus(_cancellation.Token);
        _exceptionCount = 0;
        _stopwatch.Stop();

        OnStatusUpdated(status);
      }
      catch (Exception ex)
      {
        Log.Error("Exception while polling machine status", ex);
        _cachedSession = null;

        if (++_exceptionCount >= MaxExceptionCount)
        {
          _stopwatch.Restart();
          Log.Error("Max consecutive failure count reached, throttling updates", ex);
        }
        OnStatusUpdated(new MachineStatus { MachineId = Machine.Id });
      }
    }
    finally
    {
      _pollLock.Release();
    }
  }

  async Task<MachineStatus> AcquireStatus(CancellationToken cancellation)
  {
    if (Machine is null)
      throw new InvalidOperationException("Machine is not configured");

    var status = new MachineStatus { MachineId = Machine.Id };
    var model = await FetchModel<ObjectModel>(cancellation: cancellation);

    if (model == null)
      return status;

    status.State = MapState(model.State?.Status);

    if (model.Heat != null)
      status.Temperatures = ReadTemperatures(model.Heat);

    if (status.State == MachineState.Operational || status.State == MachineState.Paused)
    {
      var extruders = await FetchModel<List<Extruder>>("move.extruders", string.Empty, cancellation);
      status.ElapsedJobTime = model.Job?.Duration ?? 0;
      var file = await FetchModel<GCodeFileInfo>("job.file", string.Empty, cancellation);

      if (extruders != null)
      {
        var (timeRemaining, progress) = CalculateCompletion(model, extruders, file);
        status.Progress = progress;
        status.EstimatedTimeRemaining = timeRemaining;
      }
    }

    return status;
  }

  protected override async Task ExecuteGCode(string command)
  {
    using var _ = await SendRequest("rr_gcode", new Dictionary<string, string> { { "gcode", command } });
  }

  async Task<T?> FetchModel<T>(string? key = null, string flags = "d99fno", CancellationToken cancellation = default)
  {
    var query = new Dictionary<string, string> { { "flags", flags } };
    if (!string.IsNullOrWhiteSpace(key))
    {
      query.Add("key", key);
    }

    var response = await SendRequest<ModelResponse<T>>("rr_model", query, cancellation);
    return response != null ? response.Result : default;
  }

  async Task<T?> SendRequest<T>(string resource, Dictionary<string, string>? query = null, CancellationToken cancellation = default)
  {
    using var response = await SendRequest(resource, query, cancellation);
    var content = await response.Content.ReadAsStringAsync(cancellation);
    if (string.IsNullOrWhiteSpace(content))
      return default;

    return JsonSerializer.Deserialize<T>(content, JsonOptions);
  }

  async Task<HttpResponseMessage> SendRequest(
    string resource,
    Dictionary<string, string>? query = null,
    CancellationToken cancellation = default,
    bool isRetry = false
  )
  {
    if (Machine is null)
      throw new InvalidOperationException("Machine is not configured");

    var password = string.IsNullOrWhiteSpace(Machine.Password) ? RepRapFirmwareMachine.DefaultPassword : Machine.Password;
    var session = await GetOrCreateSession(password, cancellation);

    var uriBuilder = new UriBuilder($"{Machine.Url}/{resource}");
    if (query != null && query.Count > 0)
    {
      var queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
      uriBuilder.Query = queryString;
    }

    using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
    request.Headers.Add("X-Session-Key", session.SessionKey.ToString());

    var httpClient = httpClientFactory.CreateClient();
    using var response = await httpClient.SendAsync(request, cancellation);

    // If the request failed and we haven't retried yet, invalidate the
    // cached session and try once more with a fresh connection.
    if (!response.IsSuccessStatusCode && !isRetry)
    {
      response.Dispose();
      _cachedSession = null;
      return await SendRequest(resource, query, cancellation, isRetry: true);
    }

    response.EnsureSuccessStatusCode();
    return response;
  }

  async Task<ConnectResponse> GetOrCreateSession(string password, CancellationToken cancellation)
  {
    if (_cachedSession != null)
      return _cachedSession;

    _cachedSession = await FetchConnect(password, cancellation);
    return _cachedSession;
  }

  async Task<ConnectResponse> FetchConnect(string password, CancellationToken cancellation)
  {
    if (Machine is null)
      throw new InvalidOperationException("Machine is not configured");

    // Password must be in the query string per the RRF HTTP API specification
    var connectUri = $"{Machine.Url}/rr_connect?password={Uri.EscapeDataString(password)}&sessionKey=yes";
    var httpClient = httpClientFactory.CreateClient();
    var connectResponseStr = await httpClient.GetStringAsync(connectUri, cancellation);
    var connectResponse = JsonSerializer.Deserialize<ConnectResponse>(connectResponseStr, JsonOptions);

    if (connectResponse == null)
      throw new InvalidOperationException("Failed to deserialize connect response");

    if (connectResponse.Err != 0)
      throw new InvalidOperationException($"rr_connect failed with error code {connectResponse.Err}");

    return connectResponse;
  }
}
