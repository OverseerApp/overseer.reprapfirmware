using System.Diagnostics;
using System.Text.Json;
using Overseer.RepRapFirmware.Models;
using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

public class RepRapFirmwareMachineProvider(IHttpClientFactory httpClientFactory) : RepRapFirmwareMachineProviderBase<RepRapFirmwareMachine>
{
  System.Timers.Timer? _timer;
  CancellationTokenSource? _cancellation;
  int _exceptionCount;
  readonly Stopwatch _stopwatch = new();
  const int MaxExceptionCount = 5;
  const int ExceptionTimeout = 2;

  public override void Start<TMachine>(int interval, TMachine machine)
  {
    _timer?.Dispose();
    _timer = new System.Timers.Timer(interval);
    _timer.Elapsed += async (sender, args) => await Poll();
    _timer.Start();
  }

  public override void Stop()
  {
    _timer?.Dispose();
    _timer = null;
    _cancellation?.Cancel();
    _cancellation?.Dispose();
  }

  public override async Task CancelJob()
  {
    await PauseJob();
    await ExecuteGCode("M0");
  }

  async Task Poll()
  {
    if (Machine is null)
      return;

    if (_stopwatch.IsRunning && _stopwatch.Elapsed.TotalMinutes < ExceptionTimeout)
    {
      OnStatusUpdated(new MachineStatus { MachineId = Machine.Id });
      return;
    }

    try
    {
      _cancellation?.Cancel();
      _cancellation?.Dispose();
      _cancellation = new CancellationTokenSource();

      var status = await AcquireStatus(_cancellation.Token);
      _exceptionCount = 0;
      _stopwatch.Stop();

      OnStatusUpdated(status);
    }
    catch (Exception)
    {
      if (++_exceptionCount >= MaxExceptionCount)
      {
        _stopwatch.Restart();
      }
      OnStatusUpdated(new MachineStatus { MachineId = Machine.Id });
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
    status.Temperatures = ReadTemperatures(model.Heat!);

    if (status.State == MachineState.Operational || status.State == MachineState.Paused)
    {
      var extruders = await FetchModel<List<Extruder>>("move.extruders", string.Empty, cancellation);
      status.ElapsedJobTime = model.Job?.Duration ?? 0;
      var file = await FetchModel<GCodeFileInfo>("job.file", string.Empty, cancellation);
      var (timeRemaining, progress) = CalculateCompletion(model, extruders!, file!);
      status.Progress = progress;
      status.EstimatedTimeRemaining = timeRemaining;
    }

    return status;
  }

  protected override Task ExecuteGCode(string command)
  {
    return Fetch("rr_gcode", new Dictionary<string, string> { { "gcode", command } });
  }

  async Task<T?> FetchModel<T>(string? key = null, string flags = "d99fno", CancellationToken cancellation = default)
  {
    var query = new Dictionary<string, string> { { "flags", flags } };
    if (!string.IsNullOrWhiteSpace(key))
    {
      query.Add("key", key);
    }

    var response = await Fetch<ModelResponse<T>>("rr_model", query, cancellation);
    return response != null ? response.Result : default;
  }

  async Task<T?> Fetch<T>(string resource, Dictionary<string, string>? query = null, CancellationToken cancellation = default)
  {
    if (Machine is null)
      throw new InvalidOperationException("Machine is not configured");

    var password = string.IsNullOrWhiteSpace(Machine.Password) ? RepRapFirmwareMachine.DefaultPassword : Machine.Password;

    var connectResponse = await FetchConnect(password, cancellation);

    var uriBuilder = new UriBuilder($"{Machine.Url}/{resource}");
    if (query != null && query.Count > 0)
    {
      var queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
      uriBuilder.Query = queryString;
    }

    var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
    request.Headers.Add("X-Session-Key", connectResponse.SessionKey.ToString());

    var httpClient = httpClientFactory.CreateClient();
    var response = await httpClient.SendAsync(request, cancellation);
    response.EnsureSuccessStatusCode();

    var content = await response.Content.ReadAsStringAsync(cancellation);
    if (string.IsNullOrWhiteSpace(content))
      return default;

    return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
  }

  async Task Fetch(string resource, Dictionary<string, string>? query = null, CancellationToken cancellation = default)
  {
    if (Machine is null)
      throw new InvalidOperationException("Machine is not configured");

    var password = string.IsNullOrWhiteSpace(Machine.Password) ? RepRapFirmwareMachine.DefaultPassword : Machine.Password;

    var connectResponse = await FetchConnect(password, cancellation);

    var uriBuilder = new UriBuilder($"{Machine.Url}/{resource}");
    if (query != null && query.Count > 0)
    {
      var queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
      uriBuilder.Query = queryString;
    }

    var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
    request.Headers.Add("X-Session-Key", connectResponse.SessionKey.ToString());

    var httpClient = httpClientFactory.CreateClient();
    var response = await httpClient.SendAsync(request, cancellation);

    response.EnsureSuccessStatusCode();
  }

  async Task<ConnectResponse> FetchConnect(string password, CancellationToken cancellation)
  {
    if (Machine is null)
      throw new InvalidOperationException("Machine is not configured");

    var connectUri = $"{Machine.Url}/rr_connect?password={Uri.EscapeDataString(password)}&sessionKey=yes";
    var httpClient = httpClientFactory.CreateClient();
    var connectResponseStr = await httpClient.GetStringAsync(connectUri, cancellation);
    var connectResponse = JsonSerializer.Deserialize<ConnectResponse>(
      connectResponseStr,
      new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
    );

    if (connectResponse == null)
      throw new InvalidOperationException("Failed to connect to machine");

    return connectResponse;
  }
}
