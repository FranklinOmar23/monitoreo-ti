using MonitoreoTI.AgentHost;
using Xunit;

namespace MonitoreoTI.AgentHost.Tests;

/// <summary>Decisión 88: la instalación remota explica hasta dónde llegó el instalador.</summary>
public class RemoteInstallTests
{
    [Fact]
    public void Without_a_log_the_installer_never_ran()
    {
        var message = RemoteInstaller.SetupFailureMessage([]);
        Assert.Contains("no llegó a ejecutarse", message);
        Assert.Contains("antivirus", message);
    }

    [Fact]
    public void A_log_with_only_the_start_means_it_stopped_before_copying()
    {
        Assert.Contains("antes de copiar", RemoteInstaller.SetupFailureMessage(["Instalador 0.4.4 iniciado.", ""]));
    }

    [Fact]
    public void Otherwise_the_last_step_is_reported()
    {
        var message = RemoteInstaller.SetupFailureMessage([
            "Instalador 0.4.4 iniciado.",
            "Archivos copiados en la carpeta del programa.",
            "Servicio del agente: código 3. El token de instalación venció.",
        ]);
        Assert.Contains("código 3. El token de instalación venció.", message);
    }
}
