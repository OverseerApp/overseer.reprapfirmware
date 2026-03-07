using System.Text.Json;
using Overseer.RepRapFirmware.Models;
using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

public class RepRapFirmwareMachineConfigurationProvider(IHttpClientFactory httpClientFactory) : IMachineConfigurationProvider<RepRapFirmwareMachine>
{
  public async Task<RepRapFirmwareMachine> Configure(Machine machine)
  {
    var updatedMachine = new RepRapFirmwareMachine(machine);

    if (string.IsNullOrWhiteSpace(updatedMachine.Url))
      throw new InvalidOperationException("Machine URL is required");

    var httpClient = httpClientFactory.CreateClient();
    var password = string.IsNullOrWhiteSpace(updatedMachine.Password) ? RepRapFirmwareMachine.DefaultPassword : updatedMachine.Password;

    var connectUri = $"{updatedMachine.Url}/rr_connect?password={Uri.EscapeDataString(password)}&sessionKey=yes";
    var connectResponseStr = await httpClient.GetStringAsync(connectUri);
    var connectResponse = JsonSerializer.Deserialize<ConnectResponse>(
      connectResponseStr,
      new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
    );

    if (connectResponse == null)
      throw new InvalidOperationException("Failed to connect to machine");

    var tools = await FetchModel<IEnumerable<Tool>>(httpClient, updatedMachine.Url, connectResponse.SessionKey, "tools", string.Empty);
    var heat = await FetchModel<Heat>(httpClient, updatedMachine.Url, connectResponse.SessionKey, "heat", string.Empty);

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

  static async Task<T?> FetchModel<T>(HttpClient httpClient, string url, long sessionKey, string? key = null, string flags = "d99fno")
  {
    var query = new Dictionary<string, string> { { "flags", flags } };
    if (!string.IsNullOrWhiteSpace(key))
      query.Add("key", key);

    var uriBuilder = new UriBuilder($"{url}/rr_model");
    if (query.Count > 0)
    {
      var queryString = string.Join("&", query.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
      uriBuilder.Query = queryString;
    }

    var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
    request.Headers.Add("X-Session-Key", sessionKey.ToString());

    var response = await httpClient.SendAsync(request);
    response.EnsureSuccessStatusCode();

    var content = await response.Content.ReadAsStringAsync();
    if (string.IsNullOrWhiteSpace(content))
      return default;

    var modelResponse = JsonSerializer.Deserialize<ModelResponse<T>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    return modelResponse != null ? modelResponse.Result : default;
  }
}
