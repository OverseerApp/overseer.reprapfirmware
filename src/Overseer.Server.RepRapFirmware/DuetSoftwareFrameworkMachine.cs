using Overseer.Server.Integration.Machines;

namespace Overseer.Server.RepRapFirmware;

public class DuetSoftwareFrameworkMachine : Machine
{
  public const string DefaultPassword = "reprap";

  public override string MachineType => "DuetSoftwareFramework";

  public string? Password { get; set; }

  public string? Url { get; set; }

  public Uri? WebSocketUri { get; set; }
}
