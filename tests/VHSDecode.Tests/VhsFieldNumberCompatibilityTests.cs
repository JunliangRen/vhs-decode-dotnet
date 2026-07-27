using VHSDecode.Core.Decode;
using Xunit;

namespace VHSDecode.Tests;

public sealed class VhsFieldNumberCompatibilityTests
{
    [Fact(DisplayName = "VHS field number follows the v0.4.0 valid-prevfield chain")]
    public void VhsFieldNumberFollowsValidPreviousFieldChain()
    {
        Assert.Equal(
            0,
            TbcFieldDecodePipeline.ResolveVhsFieldNumber(
                previousFieldNumber: null,
                previousReadLocation: null,
                readLocation: 1_000));
        Assert.Equal(
            8,
            TbcFieldDecodePipeline.ResolveVhsFieldNumber(
                previousFieldNumber: 7,
                previousReadLocation: 1_000,
                readLocation: 2_000));
        Assert.Equal(
            7,
            TbcFieldDecodePipeline.ResolveVhsFieldNumber(
                previousFieldNumber: 7,
                previousReadLocation: 1_000,
                readLocation: 1_000));
        Assert.Equal(
            7,
            TbcFieldDecodePipeline.ResolveVhsFieldNumber(
                previousFieldNumber: 7,
                previousReadLocation: 1_000,
                readLocation: 900));
    }
}
