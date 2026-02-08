using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Overseer.Server.Integration.Machines;
using Overseer.Server.RepRapFirmware.Models;

namespace Overseer.Server.RepRapFirmware;

public class RepRapFirmwareMachineProvider : IMachineProvider<RepRapFirmwareMachine>
{
  private readonly RepRapFirmwareMachine _machine;
  private System.Timers.Timer? _timer;
  private CancellationTokenSource? _cancellation;
  private X509Certificate2Collection? _clientCertificateChain;
  private int _exceptionCount;
  private readonly Stopwatch _stopwatch = new();
  private const int MaxExceptionCount = 5;
  private const int ExceptionTimeout = 2;

  public event EventHandler<MachineStatusEventArgs>? StatusUpdated;

  public string MachineType => "RepRapFirmware";

  public RepRapFirmwareMachine Machine => _machine;

  public RepRapFirmwareMachineProvider(RepRapFirmwareMachine machine)
  {
    _machine = machine;
  }

  public void Start(int interval)
  {
    _timer?.Dispose();
    _timer = new System.Timers.Timer(interval);
    _timer.Elapsed += async (sender, args) => await Poll();
    _timer.Start();
    Task.Run(Poll);
  }

  public void Stop()
  {
    _timer?.Dispose();
    _timer = null;
    _cancellation?.Cancel();
    _cancellation?.Dispose();
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
    await PauseJob();
    await ExecuteGcode("M0");
  }

  public async Task Configure(Machine machine)
  {
    var updatedMachine = (RepRapFirmwareMachine)machine;
    if (_machine.Password != updatedMachine.Password)
    {
      _machine.Password = updatedMachine.Password;
    }

    var tools = await FetchModel<IEnumerable<Tool>>("tools", string.Empty);
    var heat = await FetchModel<Heat>("heat", string.Empty);
    
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
  }

  private async Task Poll()
  {
    if (_stopwatch.IsRunning && _stopwatch.Elapsed.TotalMinutes < ExceptionTimeout)
    {
      OnStatusUpdated(new MachineStatus { MachineId = _machine.Id });
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
      OnStatusUpdated(new MachineStatus { MachineId = _machine.Id });
    }
  }

  private async Task<MachineStatus> AcquireStatus(CancellationToken cancellation)
  {
    var status = new MachineStatus { MachineId = _machine.Id };
    var model = await FetchModel<ObjectModel>(cancellation: cancellation);
    
    if (model == null)
      return status;

    status.State = model.State?.Status switch
    {
      RRFMachineStatus.Processing or RRFMachineStatus.Resuming => MachineState.Operational,
      RRFMachineStatus.Paused or RRFMachineStatus.Pausing => MachineState.Paused,
      _ => MachineState.Idle,
    };

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

  private Dictionary<int, MachineTemperatureStatus> ReadTemperatures(Heat heat)
  {
    return _machine.Tools
      .Where(m => m.ToolType == MachineToolType.Heater)
      .Select(m =>
      {
        var heater = heat.Heaters.ElementAt(m.Index);
        return new MachineTemperatureStatus
        {
          Actual = heater.Current,
          Target = heater.Active,
          HeaterIndex = m.Index,
        };
      })
      .ToDictionary(x => x.HeaterIndex);
  }

  public static (int timeRemaining, double progress) CalculateCompletion(ObjectModel model, IEnumerable<Extruder> extruders, GCodeFileInfo file)
  {
    if (file?.Filament?.Count > 0)
    {
      var totalFilament = file.Filament.Aggregate((product, next) => product + next);
      var totalExtruded = extruders.Select(x => x.RawPosition).Aggregate((product, next) => product + next);
      var progress = totalExtruded / totalFilament * 100d;
      return (model.Job?.TimesLeft?.Filament ?? 0, Math.Max(0d, Math.Round(progress, 1)));
    }

    if (model.Job?.TimesLeft?.Slicer != null && model.Job.TimesLeft?.Slicer > 0 && model.Job.Duration != null)
    {
      var estimatedTotal = model.Job.Duration + model.Job.TimesLeft?.Slicer;
      var progress = model.Job?.TimesLeft?.Slicer / estimatedTotal * 100f;
      return (model.Job?.TimesLeft?.Slicer ?? 0, Math.Max(0, Math.Round(progress ?? -1 * 100)));
    }

    var fractionPrinted = model.Job?.FilePosition / file?.Size * 100f;
    return (model.Job?.TimesLeft?.File ?? 0, fractionPrinted ?? 0);
  }

  private async Task ExecuteGcode(string command)
  {
    await Fetch("rr_gcode", new Dictionary<string, string> { { "gcode", command } });
  }

  private async Task<T?> FetchModel<T>(string? key = null, string flags = "d99fno", CancellationToken cancellation = default)
  {
    var query = new Dictionary<string, string> { { "flags", flags } };
    if (!string.IsNullOrWhiteSpace(key))
    {
      query.Add("key", key);
    }

    var response = await Fetch<ModelResponse<T>>("rr_model", query, cancellation);
    return response != null ? response.Result : default;
  }

  private async Task<T?> Fetch<T>(string resource, Dictionary<string, string>? query = null, CancellationToken cancellation = default)
  {
    var password = string.IsNullOrWhiteSpace(_machine.Password) ? RepRapFirmwareMachine.DefaultPassword : _machine.Password;
    
    using var httpClient = new HttpClient();
    if (!string.IsNullOrWhiteSpace(_machine.ClientCertificate))
    {
      var handler = new HttpClientHandler();
      var cert = GetClientCertificate(_machine.ClientCertificate);
      if (cert != null)
      {
        handler.ClientCertificates.AddRange(cert);
      }
    }

    var connectResponse = await FetchConnect(httpClient, password, cancellation);
    
    var uriBuilder = new UriBuilder($"{_machine.Url}/{resource}");
    if (query != null && query.Count > 0)
    {
      var queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
      uriBuilder.Query = queryString;
    }

    var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
    request.Headers.Add("X-Session-Key", connectResponse.SessionKey.ToString());

    var response = await httpClient.SendAsync(request, cancellation);
    response.EnsureSuccessStatusCode();

    var content = await response.Content.ReadAsStringAsync(cancellation);
    if (string.IsNullOrWhiteSpace(content))
      return default;

    return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
  }

  private async Task Fetch(string resource, Dictionary<string, string>? query = null, CancellationToken cancellation = default)
  {
    var password = string.IsNullOrWhiteSpace(_machine.Password) ? RepRapFirmwareMachine.DefaultPassword : _machine.Password;
    
    using var httpClient = new HttpClient();
    var connectResponse = await FetchConnect(httpClient, password, cancellation);
    
    var uriBuilder = new UriBuilder($"{_machine.Url}/{resource}");
    if (query != null && query.Count > 0)
    {
      var queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
      uriBuilder.Query = queryString;
    }

    var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
    request.Headers.Add("X-Session-Key", connectResponse.SessionKey.ToString());

    var response = await httpClient.SendAsync(request, cancellation);
    response.EnsureSuccessStatusCode();
  }

  private async Task<ConnectResponse> FetchConnect(HttpClient httpClient, string password, CancellationToken cancellation)
  {
    var connectUri = $"{_machine.Url}/rr_connect?password={Uri.EscapeDataString(password)}&sessionKey=yes";
    var connectResponseStr = await httpClient.GetStringAsync(connectUri, cancellation);
    var connectResponse = JsonSerializer.Deserialize<ConnectResponse>(connectResponseStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    
    if (connectResponse == null)
      throw new InvalidOperationException("Failed to connect to machine");
    
    return connectResponse;
  }

  private X509Certificate2Collection? GetClientCertificate(string? commonName)
  {
    if (string.IsNullOrWhiteSpace(commonName))
      return null;

    if (_clientCertificateChain?.Find(X509FindType.FindBySubjectName, commonName, false).Count > 0)
      return _clientCertificateChain;

    try
    {
      var certificateStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
      certificateStore.Open(OpenFlags.ReadOnly);
      _clientCertificateChain = certificateStore.Certificates.Find(X509FindType.FindBySubjectName, commonName, false);
      certificateStore.Close();
      return _clientCertificateChain;
    }
    catch
    {
      return null;
    }
  }

  private void OnStatusUpdated(MachineStatus status)
  {
    StatusUpdated?.Invoke(this, new MachineStatusEventArgs(status));
  }
}
