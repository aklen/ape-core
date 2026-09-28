using Ape.Core.Determinism;

namespace Ape.Core.Tests;

public class IngressWrapTests
{
    [Theory]
    [InlineData(100, 101, true)]
    [InlineData(100, 65000, false)]
    [InlineData(65000, 10, true)]
    [InlineData(100, 90, false)]
    [InlineData(100, 100, false)]
    [InlineData(0, 32768, false)]
    [InlineData(65535, 0, true)]
    [InlineData(32767, 0, false)]
    public void IsForward_matches_serial_number_table(long previous, long current, bool expected)
    {
        Assert.Equal(expected, IngressWrap.IsForward(previous, current));
    }

    [Fact]
    public void ForwardDelta_is_modular()
    {
        Assert.Equal(1, IngressWrap.ForwardDelta(100, 101));
        Assert.Equal(64900, IngressWrap.ForwardDelta(100, 65000));
        Assert.Equal(546, IngressWrap.ForwardDelta(65000, 10));
        Assert.Equal(65526, IngressWrap.ForwardDelta(100, 90));
        Assert.Equal(0, IngressWrap.ForwardDelta(100, 100));
        Assert.Equal(32768, IngressWrap.ForwardDelta(0, 32768));
    }

    [Fact]
    public void Validate_rejects_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IngressWrap.Validate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => IngressWrap.Validate(IngressWrap.Modulus));
        IngressWrap.Validate(0);
        IngressWrap.Validate(IngressWrap.Modulus - 1);
    }

    [Fact]
    public void Every_single_step_around_the_modulus_is_forward()
    {
        for (long prev = 0; prev < IngressWrap.Modulus; prev++)
        {
            var next = (prev + 1) % IngressWrap.Modulus;
            Assert.True(IngressWrap.IsForward(prev, next), $"expected forward {prev} -> {next}");
        }
    }

    [Fact]
    public void Half_range_and_beyond_are_never_forward()
    {
        for (long prev = 0; prev < IngressWrap.Modulus; prev += 1024)
        {
            var half = (prev + IngressWrap.HalfRange) % IngressWrap.Modulus;
            var back = (prev + IngressWrap.Modulus - 1) % IngressWrap.Modulus;
            Assert.False(IngressWrap.IsForward(prev, half));
            Assert.False(IngressWrap.IsForward(prev, back));
            Assert.False(IngressWrap.IsForward(prev, prev));
        }
    }

    [Fact]
    public void Extend_walks_past_one_wrap()
    {
        long ext = 65534;
        ext = IngressWrap.Extend(ext, 65535);
        Assert.Equal(65535, ext);
        ext = IngressWrap.Extend(ext, 0);
        Assert.Equal(65536, ext);
        ext = IngressWrap.Extend(ext, 1);
        Assert.Equal(65537, ext);
        Assert.Equal(1, IngressWrap.Raw(ext));
    }
}
