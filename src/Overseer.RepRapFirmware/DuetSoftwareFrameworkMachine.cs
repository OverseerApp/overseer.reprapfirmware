using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

[MachineType("RepRapFirmware (SBC)")]
public record DuetSoftwareFrameworkMachine : RepRapFirmwareMachine
{
  [MachineProperty(isIgnored: true)]
  public Uri? WebSocketUri
  {
    get => GetProperty<Uri?>(nameof(WebSocketUri));
    set => SetProperty(nameof(WebSocketUri), value);
  }
}
