using FluentAssertions;
using TradingBot.Application.Strategies.Indicators;
using TradingBot.Core.Enums;
using TradingBot.Core.ValueObjects;

namespace TradingBot.Application.Tests.Indicators;

public sealed class IndicatorFactoryTests
{
    [Fact]
    public void Create_WhenRsi_ReturnsRsiIndicator()
    {
        var config = IndicatorConfig.Rsi(14, 70, 30).Value;

        var indicator = IndicatorFactory.Create(config);

        indicator.Type.Should().Be(IndicatorType.RSI);
        indicator.Name.Should().Be("RSI(14)");
    }

    [Fact]
    public void Create_WhenEma_ReturnsEmaIndicator()
    {
        var config = IndicatorConfig.Ema(20).Value;

        var indicator = IndicatorFactory.Create(config);

        indicator.Type.Should().Be(IndicatorType.EMA);
        indicator.Name.Should().Be("EMA(20)");
    }

    [Fact]
    public void Create_WhenSma_ReturnsSmaIndicator()
    {
        var config = IndicatorConfig.Sma(50).Value;

        var indicator = IndicatorFactory.Create(config);

        indicator.Type.Should().Be(IndicatorType.SMA);
        indicator.Name.Should().Be("SMA(50)");
    }

    [Fact]
    public void Create_WhenMacd_ReturnsMacdIndicator()
    {
        var config = IndicatorConfig.Macd(12, 26, 9).Value;

        var indicator = IndicatorFactory.Create(config);

        indicator.Type.Should().Be(IndicatorType.MACD);
        indicator.Name.Should().Be("MACD(12,26,9)");
    }

    [Fact]
    public void Create_WhenBollingerBands_ReturnsBollingerBandsIndicator()
    {
        var config = IndicatorConfig.Bollinger(20, 2m).Value;

        var indicator = IndicatorFactory.Create(config);

        indicator.Type.Should().Be(IndicatorType.BollingerBands);
        indicator.Name.Should().Be("BB(20,2.0)");
    }

    [Fact]
    public void Create_WhenUnsupportedType_ThrowsNotSupportedException()
    {
        var config = IndicatorConfig.Create(IndicatorType.Price, new Dictionary<string, decimal>()).Value;

        var act = () => IndicatorFactory.Create(config);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Price*");
    }

    [Fact]
    public void Create_WhenAdx_ReturnsAdxIndicator()
    {
        var config = IndicatorConfig.Adx(14).Value;

        var indicator = IndicatorFactory.Create(config);

        indicator.Type.Should().Be(IndicatorType.ADX);
        indicator.Name.Should().Be("ADX(14)");
    }

    [Fact]
    public void Create_WhenAtr_ReturnsAtrIndicator()
    {
        var config = IndicatorConfig.Atr(14).Value;

        var indicator = IndicatorFactory.Create(config);

        indicator.Type.Should().Be(IndicatorType.ATR);
        indicator.Name.Should().Be("ATR(14)");
    }

    [Fact]
    public void Create_WhenVolumeSma_ReturnsVolumeSmaIndicator()
    {
        var config = IndicatorConfig.VolumeSma(20).Value;

        var indicator = IndicatorFactory.Create(config);

        indicator.Type.Should().Be(IndicatorType.Volume);
        indicator.Name.Should().Be("VolSMA(20)");
    }
}
