using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Overseer.RepRapFirmware.Models;
using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

public class DuetSoftwareFrameworkMachineProvider(IHttpClientFactory httpClientFactory)
  : RepRapFirmwareMachineProviderBase<DuetSoftwareFrameworkMachine>
{
  private ClientWebSocket? _webSocket;
  private CancellationTokenSource? _cancellationTokenSource;
  private MachineStatus? _lastStatus;
  private readonly Lock _statusLock = new();

  public override void Start<TMachine>(int interval, TMachine machine)
  {
    _cancellationTokenSource?.Cancel();
    _cancellationTokenSource?.Dispose();
    _cancellationTokenSource = new CancellationTokenSource();
    Task.Run(() => ConnectWebSocket(_cancellationTokenSource.Token));
  }

  public override void Stop()
  {
    _cancellationTokenSource?.Cancel();
    _cancellationTokenSource?.Dispose();
    _webSocket?.Dispose();
  }

  protected override async Task ExecuteGCode(string command)
  {
    if (string.IsNullOrWhiteSpace(command))
      throw new ArgumentException("Command cannot be null or empty.", nameof(command));

    if (Machine is null)
      throw new InvalidOperationException("Machine is not configured");

    var httpClient = httpClientFactory.CreateClient();
    var sessionKey = await GetSessionKey(httpClient, Machine);

    if (sessionKey != null)
    {
      httpClient.DefaultRequestHeaders.Remove("X-Session-Key");
      httpClient.DefaultRequestHeaders.Add("X-Session-Key", sessionKey);
    }

    var uri = new UriBuilder(Machine.Url!) { Path = "machine/code", Query = "async=true" }.Uri.ToString();
    var content = new StringContent(command, Encoding.UTF8, "text/plain");
    await httpClient.PostAsync(uri, content);
  }

  private static async Task<string?> GetSessionKey(HttpClient client, DuetSoftwareFrameworkMachine machine)
  {
    var password = string.IsNullOrWhiteSpace(machine.Password) ? RepRapFirmwareMachine.DefaultPassword : machine.Password;
    var connection = await client.GetFromJsonAsync<MachineConnectResponse>(
      new UriBuilder(machine.Url!) { Path = "machine/connect", Query = $"password={password}" }.Uri
    );

    return connection?.SessionKey;
  }

  private async Task ConnectWebSocket(CancellationToken cancellationToken)
  {
    if (Machine is null)
      throw new InvalidOperationException("Machine is not configured");

    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        var httpClient = httpClientFactory.CreateClient();
        var sessionKey = await GetSessionKey(httpClient, Machine);
        if (sessionKey == null)
        {
          await Task.Delay(5000, cancellationToken);
          continue;
        }

        var wsUri = new UriBuilder(Machine.WebSocketUri!) { Query = $"sessionKey={sessionKey}" }.Uri;
        await _webSocket.ConnectAsync(wsUri, cancellationToken);

        await ReceiveMessages(cancellationToken);
      }
      catch (Exception) when (!cancellationToken.IsCancellationRequested)
      {
        OnStatusUpdated(new MachineStatus { MachineId = Machine.Id });
        await Task.Delay(5000, cancellationToken);
      }
    }
  }

  private async Task ReceiveMessages(CancellationToken cancellationToken)
  {
    var buffer = new byte[1024 * 4];

    while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
    {
      var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

      if (result.MessageType == WebSocketMessageType.Close)
      {
        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken);
        break;
      }

      var messageText = Encoding.UTF8.GetString(buffer, 0, result.Count);
      await HandleIncomingMessage(messageText, cancellationToken);
    }
  }

  async Task HandleIncomingMessage(string messageText, CancellationToken cancellationToken)
  {
    await AcknowledgeMessage(cancellationToken);

    if (string.IsNullOrWhiteSpace(messageText))
      return;

    if (Machine is null)
      return;

    var model = JsonSerializer.Deserialize<ObjectModel>(messageText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (model == null)
      return;

    var nextStatus = new MachineStatus { MachineId = Machine.Id, State = MapState(model.State?.Status, _lastStatus?.State) };

    if (model.Heat != null)
    {
      nextStatus.Temperatures = ReadTemperatures(model.Heat);
    }
    else
    {
      nextStatus.Temperatures = _lastStatus?.Temperatures ?? [];
    }

    if (nextStatus.State == MachineState.Operational || nextStatus.State == MachineState.Paused)
    {
      nextStatus.ElapsedJobTime = model.Job?.Duration ?? _lastStatus?.ElapsedJobTime ?? 0;
      var file = model.Job?.File;

      if (file != null)
      {
        var extruders = model.Move?.Extruders ?? new List<Extruder>();
        var (timeRemaining, progress) = CalculateCompletion(model, extruders, file);
        nextStatus.Progress = progress;
        nextStatus.EstimatedTimeRemaining = timeRemaining;
      }
      else
      {
        nextStatus.Progress = _lastStatus?.Progress ?? 0;
        nextStatus.EstimatedTimeRemaining = _lastStatus?.EstimatedTimeRemaining ?? 0;
      }
    }

    lock (_statusLock)
    {
      if (_lastStatus != null && _lastStatus.Equals(nextStatus))
        return;

      _lastStatus = nextStatus;
    }

    OnStatusUpdated(nextStatus);
  }

  async Task AcknowledgeMessage(CancellationToken cancellationToken)
  {
    if (_webSocket == null)
      return;

    var message = new ArraySegment<byte>(Encoding.UTF8.GetBytes("OK\n"));
    await _webSocket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
  }

  protected override Dictionary<int, MachineTemperatureStatus> ReadTemperatures(Heat heat)
  {
    return Machine
        ?.Tools.Where(m => m.ToolType == MachineToolType.Heater)
        .Select(m =>
        {
          var heater = heat.Heaters.ElementAt(m.Index);

          if (heater.Current == 0)
          {
            return _lastStatus?.Temperatures?.GetValueOrDefault(m.Index)
              ?? new MachineTemperatureStatus
              {
                Actual = 0,
                Target = 0,
                HeaterIndex = m.Index,
              };
          }

          return new MachineTemperatureStatus
          {
            Actual = heater.Current,
            Target = heater.Active,
            HeaterIndex = m.Index,
          };
        })
        .ToDictionary(x => x.HeaterIndex) ?? [];
  }
}
