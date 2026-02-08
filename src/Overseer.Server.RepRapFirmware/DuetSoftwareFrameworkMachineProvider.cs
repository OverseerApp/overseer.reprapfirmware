using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Overseer.Server.Integration.Machines;
using Overseer.Server.RepRapFirmware.Models;

namespace Overseer.Server.RepRapFirmware;

public class DuetSoftwareFrameworkMachineProvider : IMachineProvider<DuetSoftwareFrameworkMachine>
{
  private readonly DuetSoftwareFrameworkMachine _machine;
  private ClientWebSocket? _webSocket;
  private CancellationTokenSource? _cancellationTokenSource;
  private MachineStatus? _lastStatus;
  private readonly object _statusLock = new();
  private DateTime _lastStatusUpdate = DateTime.MinValue;

  public event EventHandler<MachineStatusEventArgs>? StatusUpdated;

  public string MachineType => "DuetSoftwareFramework";

  public DuetSoftwareFrameworkMachine Machine => _machine;

  public DuetSoftwareFrameworkMachineProvider(DuetSoftwareFrameworkMachine machine)
  {
    _machine = machine;
  }

  public void Start(int interval)
  {
    _cancellationTokenSource?.Cancel();
    _cancellationTokenSource?.Dispose();
    _cancellationTokenSource = new CancellationTokenSource();
    Task.Run(() => ConnectWebSocket(_cancellationTokenSource.Token));
  }

  public void Stop()
  {
    _cancellationTokenSource?.Cancel();
    _cancellationTokenSource?.Dispose();
    _webSocket?.Dispose();
  }

  public async Task PauseJob()
  {
    await ExecuteGcode("M25");
  }

  public async Task ResumeJob()
  {
    await ExecuteGcode("M24");
  }

  public async Task CancelJob()
  {
    await ExecuteGcode("M0");
  }

  public async Task Configure(Machine machine)
  {
    var updatedMachine = (DuetSoftwareFrameworkMachine)machine;
    
    if (string.IsNullOrWhiteSpace(updatedMachine.Url))
    {
      throw new InvalidOperationException("Machine URL is required");
    }

    updatedMachine.Url = new UriBuilder(updatedMachine.Url)
    {
      Path = "/",
      Scheme = "http",
      Port = 80,
    }.Uri.ToString();

    using var client = new HttpClient();
    var sessionKey = await GetSessionKey(client, updatedMachine);
    
    if (sessionKey != null)
    {
      client.DefaultRequestHeaders.Remove("X-Session-Key");
      client.DefaultRequestHeaders.Add("X-Session-Key", sessionKey);
    }

    updatedMachine.WebSocketUri = new UriBuilder(updatedMachine.Url) { Path = "machine", Scheme = "ws" }.Uri;

    var model = await client.GetFromJsonAsync<ObjectModel>(new UriBuilder(updatedMachine.Url) { Path = "machine/model" }.Uri);
    var tools = model?.Tools;
    var heat = model?.Heat;

    if (tools == null || heat == null)
      throw new InvalidOperationException("Failed to connect to machine");

    var machineTools = new List<MachineTool>();
    machineTools.AddRange(heat.BedHeaters.Where(i => i >= 0).Select(i => new MachineTool(MachineToolType.Heater, i, "bed")));
    machineTools.AddRange(heat.ChamberHeaters.Where(i => i >= 0).Select(i => new MachineTool(MachineToolType.Heater, i, "chamber")));

    foreach (var tool in tools)
    {
      machineTools.AddRange(tool.Heaters.Select(i => new MachineTool(MachineToolType.Heater, i)));
      machineTools.AddRange(tool.Extruders.Select(i => new MachineTool(MachineToolType.Extruder, i)));
    }

    updatedMachine.Tools = machineTools;
    _machine.Tools = machineTools;
    _machine.Url = updatedMachine.Url;
    _machine.WebSocketUri = updatedMachine.WebSocketUri;
  }

  public async Task ExecuteGcode(string command)
  {
    if (string.IsNullOrWhiteSpace(command))
      throw new ArgumentException("Command cannot be null or empty.", nameof(command));

    using var client = new HttpClient();
    var sessionKey = await GetSessionKey(client, _machine);
    
    if (sessionKey != null)
    {
      client.DefaultRequestHeaders.Remove("X-Session-Key");
      client.DefaultRequestHeaders.Add("X-Session-Key", sessionKey);
    }

    var uri = new UriBuilder(_machine.Url!) { Path = "machine/code", Query = "async=true" }.Uri.ToString();
    var content = new StringContent(command, Encoding.UTF8, "text/plain");
    await client.PostAsync(uri, content);
  }

  private async Task<string?> GetSessionKey(HttpClient client, DuetSoftwareFrameworkMachine machine)
  {
    var password = string.IsNullOrWhiteSpace(machine.Password) ? DuetSoftwareFrameworkMachine.DefaultPassword : machine.Password;
    var connection = await client.GetFromJsonAsync<MachineConnectResponse>(
      new UriBuilder(machine.Url!) { Path = "machine/connect", Query = $"password={password}" }.Uri
    );

    return connection?.SessionKey;
  }

  private async Task ConnectWebSocket(CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        var sessionKey = await GetSessionKey(new HttpClient(), _machine);
        if (sessionKey == null)
        {
          await Task.Delay(5000, cancellationToken);
          continue;
        }

        var wsUri = new UriBuilder(_machine.WebSocketUri!) { Query = $"sessionKey={sessionKey}" }.Uri;
        await _webSocket.ConnectAsync(wsUri, cancellationToken);

        await ReceiveMessages(cancellationToken);
      }
      catch (Exception) when (!cancellationToken.IsCancellationRequested)
      {
        OnStatusUpdated(new MachineStatus { MachineId = _machine.Id });
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

  private async Task HandleIncomingMessage(string messageText, CancellationToken cancellationToken)
  {
    await AcknowledgeMessage(cancellationToken);

    if (string.IsNullOrWhiteSpace(messageText))
      return;

    var message = JsonSerializer.Deserialize<ObjectModel>(messageText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    
    if (message == null)
      return;

    await ProcessStatusUpdate(message);
  }

  private async Task AcknowledgeMessage(CancellationToken cancellationToken)
  {
    if (_webSocket == null)
      return;

    var message = new ArraySegment<byte>(Encoding.UTF8.GetBytes("OK\n"));
    await _webSocket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
  }

  private Task ProcessStatusUpdate(ObjectModel model)
  {
    if (model == null)
      return Task.CompletedTask;

    var nextStatus = new MachineStatus
    {
      MachineId = _machine.Id,
      State = model.State?.Status switch
      {
        RRFMachineStatus.Processing or RRFMachineStatus.Resuming => MachineState.Operational,
        RRFMachineStatus.Paused or RRFMachineStatus.Pausing => MachineState.Paused,
        _ => _lastStatus?.State ?? MachineState.Idle,
      },
    };

    if (model.Heat != null)
    {
      nextStatus.Temperatures = ReadTemperatures(model.Heat);
    }
    else
    {
      nextStatus.Temperatures = _lastStatus?.Temperatures ?? new Dictionary<int, MachineTemperatureStatus>();
    }

    if (nextStatus.State == MachineState.Operational || nextStatus.State == MachineState.Paused)
    {
      nextStatus.ElapsedJobTime = model.Job?.Duration ?? _lastStatus?.ElapsedJobTime ?? 0;
      var file = model.Job?.File;
      
      if (file != null)
      {
        var extruders = model.Move?.Extruders ?? new List<Extruder>();
        var (timeRemaining, progress) = RepRapFirmwareMachineProvider.CalculateCompletion(model, extruders, file);
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
        return Task.CompletedTask;

      _lastStatus = nextStatus;
      _lastStatusUpdate = DateTime.UtcNow;
    }

    OnStatusUpdated(nextStatus);
    return Task.CompletedTask;
  }

  private Dictionary<int, MachineTemperatureStatus> ReadTemperatures(Heat heat)
  {
    return _machine.Tools
      .Where(m => m.ToolType == MachineToolType.Heater)
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
      .ToDictionary(x => x.HeaterIndex);
  }

  private void OnStatusUpdated(MachineStatus status)
  {
    StatusUpdated?.Invoke(this, new MachineStatusEventArgs(status));
  }
}
