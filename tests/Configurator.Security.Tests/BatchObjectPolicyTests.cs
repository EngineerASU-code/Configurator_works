using Configurator.Core.Workspace;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class BatchObjectPolicyTests
{
    [Fact]
    public void Next_record_uses_existing_and_pending_reservations_for_selected_plc()
    {
        var next = BatchObjectPolicy.NextFreeRecord("2",
            new[] { "2|1", "2|2", "1|90" },
            new[] { "2|3" });
        Assert.Equal(4, next);
    }

    [Fact]
    public void Record_reservation_is_scoped_to_plc()
    {
        var next = BatchObjectPolicy.NextFreeRecord("3",
            new[] { "2|1", "2|2" },
            Array.Empty<string>());
        Assert.Equal(1, next);
    }

    [Fact]
    public void Validation_detects_duplicate_names_and_record_keys_before_save()
    {
        var errors = BatchObjectPolicy.ValidateRows(
            new[] { ("1", "2", "Valve_A"), ("1", "2", "valve_a") },
            new[] { "Existing" },
            new[] { "2|1" },
            hasRecord: true,
            requirePlc: true,
            validPlcKeys: new[] { "2" });
        Assert.Contains(errors, e => e.Contains("имя", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("Record", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Record_conflicts_are_detected_even_when_numeric_format_differs()
    {
        var errors = BatchObjectPolicy.ValidateRows(
            new[] { ("01", "2", "Valve_A") },
            Array.Empty<string>(),
            new[] { "2|1" },
            hasRecord: true,
            requirePlc: true,
            validPlcKeys: new[] { "2" });
        Assert.Contains(errors, e => e.Contains("Record", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validation_rejects_missing_or_unknown_plc()
    {
        var errors = BatchObjectPolicy.ValidateRows(
            new[] { ("1", "99", "Valve_A") },
            Array.Empty<string>(), Array.Empty<string>(),
            hasRecord: true, requirePlc: true, validPlcKeys: new[] { "2" });
        Assert.Contains(errors, e => e.Contains("PLC", StringComparison.OrdinalIgnoreCase));
    }
}
