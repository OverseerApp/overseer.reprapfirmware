using Overseer.Server.Integration.Machines;

namespace Overseer.Server.RepRapFirmware;

public class RepRapFirmwareMachine : Machine
{
  public const string DefaultPassword = "reprap";

  public override string MachineType => "RepRapFirmware";

  public string? Password { get; set; }

  public string? Url { get; set; }

  public string? ClientCertificate { get; set; }
}
