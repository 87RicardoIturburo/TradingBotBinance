using FluentAssertions;
using TradingBot.Core.Common;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Core.Tests.ValueObjects;

public sealed class SymbolTests
{
    [Fact]
    public void Create_WithValidValue_ReturnsSuccess()
    {
        var result = Symbol.Create("btcusdt");

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("BTCUSDT");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyValue_ReturnsFailure(string? value)
    {
        var result = Symbol.Create(value);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public void Create_WithSpecialChars_ReturnsFailure()
    {
        var result = Symbol.Create("BTC-USDT");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_WithSingleChar_ReturnsFailure()
    {
        var result = Symbol.Create("B");

        result.IsFailure.Should().BeTrue();
    }
}

public sealed class PriceTests
{
    [Fact]
    public void Create_WithPositiveValue_ReturnsSuccess()
    {
        var result = Price.Create(100.50m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(100.50m);
    }

    [Fact]
    public void Create_WithZero_ReturnsSuccess()
    {
        var result = Price.Create(0m);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_WithNegative_ReturnsFailure()
    {
        var result = Price.Create(-1m);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void PercentageChangeTo_CalculatesCorrectly()
    {
        var from = Price.Create(100m).Value;
        var to   = Price.Create(110m).Value;

        var change = from.PercentageChangeTo(to);

        change.Should().Be(10m);
    }
}

public sealed class QuantityTests
{
    [Fact]
    public void Create_WithPositiveValue_ReturnsSuccess()
    {
        var result = Quantity.Create(0.5m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(0.5m);
    }

    [Fact]
    public void Create_WithZero_ReturnsFailure()
    {
        var result = Quantity.Create(0m);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_WithNegative_ReturnsFailure()
    {
        var result = Quantity.Create(-1m);

        result.IsFailure.Should().BeTrue();
    }
}

public sealed class RiskConfigTests
{
    [Fact]
    public void Create_WithValidParams_ReturnsSuccess()
    {
        var result = RiskConfig.Create(100m, 500m, 2m, 4m, 3);

        result.IsSuccess.Should().BeTrue();
        result.Value.MaxOrderAmountUsdt.Should().Be(100m);
        result.Value.MaxOpenPositions.Should().Be(3);
    }

    [Fact]
    public void Create_WithZeroMaxOrder_ReturnsFailure()
    {
        var result = RiskConfig.Create(0m, 500m, 2m, 4m, 3);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_WithZeroMaxPositions_ReturnsFailure()
    {
        var result = RiskConfig.Create(100m, 500m, 2m, 4m, 0);

        result.IsFailure.Should().BeTrue();
    }
}

public sealed class IndicatorConfigTests
{
    [Fact]
    public void Rsi_WithDefaults_ReturnsValidConfig()
    {
        var result = IndicatorConfig.Rsi();

        result.IsSuccess.Should().BeTrue();
        result.Value.Type.Should().Be(IndicatorType.RSI);
        result.Value.GetParameter("period").Should().Be(14m);
    }

    [Fact]
    public void Rsi_WithInvalidPeriod_ReturnsFailure()
    {
        var result = IndicatorConfig.Rsi(period: 1);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Macd_WithSlowLessThanFast_ReturnsFailure()
    {
        var result = IndicatorConfig.Macd(fastPeriod: 26, slowPeriod: 12);

        result.IsFailure.Should().BeTrue();
    }
}
