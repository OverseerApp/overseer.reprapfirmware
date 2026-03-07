using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

[MachineType("RepRapFirmware (SBC)")]
public record DuetSoftwareFrameworkMachine : RepRapFirmwareMachine
{
  public DuetSoftwareFrameworkMachine() { }

  public DuetSoftwareFrameworkMachine(Machine machine)
    : base(machine) { }

  [MachineProperty(isIgnored: true)]
  public string? WebSocketUri
  {
    get => GetProperty<string?>(nameof(WebSocketUri));
    set => SetProperty(nameof(WebSocketUri), value);
  }
}
