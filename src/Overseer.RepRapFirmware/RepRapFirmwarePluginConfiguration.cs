using Microsoft.Extensions.DependencyInjection;
using Overseer.Server.Integration;
using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

public class RepRapFirmwarePluginConfiguration : IPluginConfiguration
{
  public void ConfigureServices(IServiceCollection services)
  {
    services.AddHttpClient();
    services.AddTransient<IMachineProvider<RepRapFirmwareMachine>, RepRapFirmwareMachineProvider>();
    services.AddTransient<IMachineProvider<DuetSoftwareFrameworkMachine>, DuetSoftwareFrameworkMachineProvider>();
    services.AddTransient<IMachineConfigurationProvider<RepRapFirmwareMachine>, RepRapFirmwareMachineConfigurationProvider>();
    services.AddTransient<IMachineConfigurationProvider<DuetSoftwareFrameworkMachine>, DuetSoftwareFrameworkMachineConfigurationProvider>();
  }
}
