using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminRetentionOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("weekly")]
    [InlineData("forever")]
    public void Audit_retention_rejects_absent_or_unknown_mode(string? mode)
    {
        var options = new AdminAuditOptions { RetentionMode = mode, RetentionDays = 30 };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30)]
    public void Audit_retention_days_must_be_positive_when_mode_is_days(int retentionDays)
    {
        var options = new AdminAuditOptions { RetentionMode = AdminAuditOptions.DaysMode, RetentionDays = retentionDays };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Audit_retention_days_mode_with_positive_days_is_valid()
    {
        var options = new AdminAuditOptions { RetentionMode = AdminAuditOptions.DaysMode, RetentionDays = 30 };

        options.Validate();

        Assert.False(options.IsIndefinite);
        Assert.Equal(AdminAuditOptions.DaysMode, options.NormalizedMode);
    }

    [Fact]
    public void Audit_retention_indefinite_mode_ignores_days()
    {
        var options = new AdminAuditOptions { RetentionMode = AdminAuditOptions.IndefiniteMode, RetentionDays = 0 };

        options.Validate();

        Assert.True(options.IsIndefinite);
    }

    [Fact]
    public void Audit_retention_mode_comparison_is_case_insensitive()
    {
        var options = new AdminAuditOptions { RetentionMode = "INDEFINITE", RetentionDays = 0 };

        options.Validate();

        Assert.True(options.IsIndefinite);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-24)]
    public void Operations_retention_hours_must_be_positive(int retentionHours)
    {
        var options = new AdminOperationsOptions { RetentionHours = retentionHours };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Operations_retention_hours_positive_is_valid()
    {
        var options = new AdminOperationsOptions { RetentionHours = 24 };

        options.Validate();
    }
}
