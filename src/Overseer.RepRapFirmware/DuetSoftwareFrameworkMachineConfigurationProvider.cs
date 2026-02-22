using System.Net.Http.Json;
using Overseer.RepRapFirmware.Models;
using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

public class DuetSoftwareFrameworkMachineConfigurationProvider(IHttpClientFactory httpClientFactory)
  : IMachineConfigurationProvider<DuetSoftwareFrameworkMachine>
{
  public async Task<DuetSoftwareFrameworkMachine> Configure(Machine machine)
  {
    var updatedMachine = (DuetSoftwareFrameworkMachine)machine;

    if (string.IsNullOrWhiteSpace(updatedMachine.Url))
      throw new InvalidOperationException("Machine URL is required");

    updatedMachine.Url = new UriBuilder(updatedMachine.Url)
    {
      Path = "/",
      Scheme = "http",
      Port = 80,
    }.Uri.ToString();

    using var httpClient = httpClientFactory.CreateClient();
    var sessionKey = await GetSessionKey(httpClient, updatedMachine);

    if (sessionKey != null)
    {
      httpClient.DefaultRequestHeaders.Remove("X-Session-Key");
      httpClient.DefaultRequestHeaders.Add("X-Session-Key", sessionKey);
    }

    updatedMachine.WebSocketUri = new UriBuilder(updatedMachine.Url) { Path = "machine", Scheme = "ws" }.Uri;

    var model = await httpClient.GetFromJsonAsync<ObjectModel>(new UriBuilder(updatedMachine.Url) { Path = "machine/model" }.Uri);
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

    return updatedMachine;
  }

  static async Task<string?> GetSessionKey(HttpClient client, DuetSoftwareFrameworkMachine machine)
  {
    var password = string.IsNullOrWhiteSpace(machine.Password) ? DuetSoftwareFrameworkMachine.DefaultPassword : machine.Password;
    var connection = await client.GetFromJsonAsync<MachineConnectResponse>(
      new UriBuilder(machine.Url!) { Path = "machine/connect", Query = $"password={password}" }.Uri
    );

    return connection?.SessionKey;
  }
}
