using Microsoft.Extensions.DependencyInjection;
using Overseer.Server.Integration.Machines;

namespace Overseer.Server.RepRapFirmware;

public static class ServiceCollectionExtensions
{
  public static IServiceCollection AddRepRapFirmwareProvider(this IServiceCollection services)
  {
    services.AddTransient<MachineProviderFactory<RepRapFirmwareMachine, RepRapFirmwareMachineProvider>>(
      sp => machine => new RepRapFirmwareMachineProvider(machine)
    );
    
    services.AddTransient<MachineProviderFactory<DuetSoftwareFrameworkMachine, DuetSoftwareFrameworkMachineProvider>>(
      sp => machine => new DuetSoftwareFrameworkMachineProvider(machine)
    );
    
    return services;
  }
}
