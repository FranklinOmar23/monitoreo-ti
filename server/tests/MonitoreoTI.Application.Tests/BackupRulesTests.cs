using FluentAssertions;
using MonitoreoTI.Application.Backups;

namespace MonitoreoTI.Application.Tests;

/// <summary>Respaldos de servidores (decisión 82): horarios, alertas, validación y lo que se firma.</summary>
public class BackupRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static BackupJobSnapshot Job(
        string? status = BackupRules.Ok, DateTime? lastOk = null, string frequency = BackupRules.Daily, string? days = null,
        DateTime? diskMissing = null, string? stuck = null, bool active = true) =>
        new(5, "ERP a disco externo", active, frequency, days, Now.AddDays(-30), status, lastOk ?? Now.AddHours(-12), "Detalle", diskMissing, stuck);

    [Theory]
    [InlineData(BackupRules.Daily, null, 26)]
    [InlineData(BackupRules.Weekly, "1,3,5", 3 * 24 + 2)] // viernes → lunes
    [InlineData(BackupRules.Weekly, "0", 7 * 24 + 2)]
    [InlineData(BackupRules.Weekly, "0,1,2,3,4,5,6", 26)]
    public void El_atraso_se_mide_por_el_hueco_mas_largo_del_horario(string frequency, string? days, int hours) =>
        BackupRules.MaxGap(frequency, days).Should().Be(TimeSpan.FromHours(hours));

    [Fact]
    public void Un_respaldo_al_dia_no_genera_alertas() =>
        BackupRules.Evaluate([Job()], Now).Should().BeEmpty();

    [Fact]
    public void Un_respaldo_fallido_es_critico_y_no_se_duplica_como_atrasado()
    {
        var findings = BackupRules.Evaluate([Job(status: BackupRules.Failed, lastOk: Now.AddDays(-3))], Now);
        findings.Should().ContainSingle().Which.Code.Should().Be("respaldo_fallido:5");
    }

    [Fact]
    public void Sin_respaldo_correcto_en_mas_de_un_dia_esta_atrasado() =>
        BackupRules.Evaluate([Job(lastOk: Now.AddHours(-27))], Now).Should().ContainSingle()
            .Which.Code.Should().Be("respaldo_atrasado:5");

    [Fact]
    public void Servicios_sin_arrancar_y_disco_ausente_tienen_su_alerta()
    {
        var findings = BackupRules.Evaluate([Job(diskMissing: Now.AddMinutes(-30), stuck: "ERPService")], Now);
        findings.Select(f => f.Code).Should().BeEquivalentTo("respaldo_servicios:5", "respaldo_sin_disco:5");
        findings.Single(f => f.Code == "respaldo_servicios:5").Level.Should().Be(Health.FindingLevel.Critical);
    }

    [Fact]
    public void Un_trabajo_pausado_no_alerta() =>
        BackupRules.Evaluate([Job(status: BackupRules.Failed, active: false)], Now).Should().BeEmpty();

    [Theory]
    [InlineData("0.4.0", true)]
    [InlineData("0.4.2", true)]
    [InlineData("0.3.14", false)]
    [InlineData("0.4.0-clasico", false)]
    [InlineData(null, false)]
    public void Solo_el_agente_nuevo_hace_respaldos(string? version, bool expected) =>
        BackupRules.SupportsBackups(version).Should().Be(expected);

    [Fact]
    public void La_firma_cubre_el_equipo_y_todo_el_contenido()
    {
        var a = BackupRules.ConfigPayload(7, "{\"trabajos\":[]}");
        a.Should().StartWith("monitoreoti/respaldos/v1\n7\n");
        BackupRules.ConfigPayload(8, "{\"trabajos\":[]}").Should().NotBe(a);
        BackupRules.ConfigPayload(7, "{\"trabajos\":[1]}").Should().NotBe(a);
        BackupRules.RunNowPayload(7, 3, "u", true, "2026").Should().NotBe(BackupRules.RunNowPayload(7, 3, "u", false, "2026"));
    }

    private static BackupJobInput Input(
        string source = BackupRules.Sql, string[]? items = null, string destination = BackupRules.ExternalDisk, string? path = "Centinela",
        string mode = BackupRules.Hot, string[]? services = null, string frequency = BackupRules.Daily, int[]? days = null) =>
        new("ERP", source, source == BackupRules.Sql ? "MSSQLSERVER" : null, items ?? ["ERP_Caribe"], mode, services, 60,
            BackupRules.Incremental, 7, destination, path, destination == BackupRules.ExternalDisk ? [new BackupDisk("WD-123", "WD Elements")] : null,
            true, null, null, frequency, days, "23:00", 7, 3, true, true);

    [Fact]
    public void El_caso_de_la_empresa_de_prueba_es_valido() =>
        new BackupJobInputValidator().Validate(Input()).IsValid.Should().BeTrue();

    [Theory]
    [InlineData(BackupRules.Folders, "D:\\Datos", true)]
    [InlineData(BackupRules.Folders, "Datos", false)]
    [InlineData(BackupRules.Folders, "D:\\..\\Windows", false)]
    [InlineData(BackupRules.Sql, "ERP]; DROP", false)]
    public void Las_carpetas_van_con_ruta_completa_y_las_bases_sin_corchetes(string source, string item, bool valid) =>
        new BackupJobInputValidator().Validate(Input(source: source, items: [item])).IsValid.Should().Be(valid);

    [Theory]
    [InlineData(BackupRules.Network, "\\\\nas\\respaldos", true)]
    [InlineData(BackupRules.Network, "nas\\respaldos", false)]
    [InlineData(BackupRules.Local, "E:\\Respaldos", true)]
    [InlineData(BackupRules.Local, "\\\\nas\\x", false)]
    [InlineData(BackupRules.ExternalDisk, "Centinela", true)]
    [InlineData(BackupRules.ExternalDisk, "..\\otra", false)]
    [InlineData(BackupRules.ExternalDisk, "E:\\x", false)]
    public void El_destino_se_valida_segun_su_tipo(string destination, string path, bool valid) =>
        new BackupJobInputValidator().Validate(Input(destination: destination, path: path)).IsValid.Should().Be(valid);

    [Fact]
    public void En_frio_hay_que_elegir_servicios_y_en_semanal_los_dias()
    {
        var validator = new BackupJobInputValidator();
        validator.Validate(Input(mode: BackupRules.Cold)).IsValid.Should().BeFalse();
        validator.Validate(Input(mode: BackupRules.Cold, services: ["ERPService"])).IsValid.Should().BeTrue();
        validator.Validate(Input(mode: BackupRules.Cold, services: ["ERP & calc"])).IsValid.Should().BeFalse();
        validator.Validate(Input(frequency: BackupRules.Weekly)).IsValid.Should().BeFalse();
        validator.Validate(Input(frequency: BackupRules.Weekly, days: [1, 5])).IsValid.Should().BeTrue();
    }
}
