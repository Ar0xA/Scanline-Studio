using System.Text.Json;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>CRC and PRBS against vectors produced by QSSTV's own code (qsstv-drm-harness
/// `crcprbs`, tables/crc_prbs.json).</summary>
public class CrcPrbsTests
{
    [Fact]
    public void TxCrc_AddByte_MatchesQsstvForAllDegrees()
    {
        using JsonDocument doc = GoldenFixtures.LoadTable("crc_prbs.json");
        int n = 0;
        foreach (JsonElement e in doc.RootElement.GetProperty("ccrc").EnumerateArray())
        {
            var crc = new DrmCrc();
            crc.Reset(e.GetProperty("degree").GetInt32());
            foreach (byte b in GoldenFixtures.Hex(e.GetProperty("data").GetString()!))
                crc.AddByte(b);
            Assert.Equal(e.GetProperty("crc").GetUInt32(), crc.GetCrc());
            n++;
        }

        Assert.Equal(112, n);
    }

    [Fact]
    public void TxCrc_AddBit_MatchesQsstv()
    {
        using JsonDocument doc = GoldenFixtures.LoadTable("crc_prbs.json");
        foreach (JsonElement e in doc.RootElement.GetProperty("ccrc_bits").EnumerateArray())
        {
            var crc = new DrmCrc();
            crc.Reset(e.GetProperty("degree").GetInt32());
            foreach (byte b in GoldenFixtures.Hex(e.GetProperty("data").GetString()!))
            {
                for (int k = 7; k >= 0; k--)
                    crc.AddBit(((b >> k) & 1) != 0);
            }

            Assert.Equal(e.GetProperty("crc").GetUInt32(), crc.GetCrc());
        }
    }

    [Fact]
    public void TxCrc16_CheckValue_IsCrc16Genibus()
    {
        // x^16+x^12+x^5+1, init 0xFFFF, MSB first, complemented: catalogue check value 0xD64E.
        var crc = new DrmCrc();
        crc.Reset(16);
        foreach (byte b in "123456789"u8)
            crc.AddByte(b);
        Assert.Equal(0xD64Eu, crc.GetCrc());
    }

    [Fact]
    public void TxCrc_GetCrcTwice_ComplementsAgainLikeQsstv()
    {
        var crc = new DrmCrc();
        crc.Reset(8);
        crc.AddByte(0x42);
        uint first = crc.GetCrc();
        uint second = crc.GetCrc();
        Assert.Equal(first ^ 0xFFu, second);
    }

    [Fact]
    public void TxCrc_UndefinedDegree_Throws()
    {
        var crc = new DrmCrc();
        Assert.Throws<ArgumentOutOfRangeException>(() => crc.Reset(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => crc.Reset(17));
    }

    [Fact]
    public void RxCrc16Bytewise_MatchesQsstv()
    {
        using JsonDocument doc = GoldenFixtures.LoadTable("crc_prbs.json");
        int zeros = 0;
        foreach (JsonElement e in doc.RootElement.GetProperty("crc16_bytewise").EnumerateArray())
        {
            int expected = (int)e.GetProperty("checksum").GetDouble();
            Assert.Equal(expected, RxCrc.Crc16Bytewise(GoldenFixtures.Hex(e.GetProperty("data").GetString()!)));
            if (expected == 0)
                zeros++;
        }

        Assert.True(zeros >= 10, "data + TX CRC16 must check to 0");
    }

    [Fact]
    public void RxCrc16Bytewise_ShortInput_IsNeverValid()
    {
        Assert.NotEqual(0, RxCrc.Crc16Bytewise([]));
        Assert.NotEqual(0, RxCrc.Crc16Bytewise([0x12]));
    }

    [Fact]
    public void RxCrc8_MatchesQsstv()
    {
        using JsonDocument doc = GoldenFixtures.LoadTable("crc_prbs.json");
        int valid = 0;
        foreach (JsonElement e in doc.RootElement.GetProperty("crc8_c").EnumerateArray())
        {
            double[] bits = e.GetProperty("bits").GetString()!.Select(c => c == '1' ? 1.0 : 0.0).ToArray();
            int expected = (int)e.GetProperty("checksum").GetDouble();
            Assert.Equal(expected, RxCrc.Crc8Bits(bits));
            if (expected == 0)
                valid++;
        }

        Assert.True(valid >= 5);
    }

    [Fact]
    public void EnergyDispersal_MatchesQsstvPrbs()
    {
        using JsonDocument doc = GoldenFixtures.LoadTable("crc_prbs.json");
        foreach (JsonElement e in doc.RootElement.GetProperty("prbs").EnumerateArray())
        {
            var bits = new byte[e.GetProperty("numBits").GetInt32()];
            EnergyDispersal.Apply(bits, e.GetProperty("lengthVSPP").GetInt32());
            Assert.Equal(GoldenFixtures.Bits(e.GetProperty("bits").GetString()!), bits);
        }
    }

    [Fact]
    public void EnergyDispersal_AppliedTwice_RestoresData()
    {
        byte[] data = Enumerable.Range(0, 900).Select(i => (byte)((i * 7 / 3) & 1)).ToArray();
        byte[] copy = (byte[])data.Clone();
        EnergyDispersal.Apply(copy, 400);
        Assert.NotEqual(data, copy);
        EnergyDispersal.Apply(copy, 400);
        Assert.Equal(data, copy);
    }

    [Fact]
    public void RxPrbs_GeneratesSameSequenceAsTx()
    {
        var tx = new byte[1024];
        EnergyDispersal.Apply(tx, 0);
        RxPrbs rx = RxPrbs.Create();
        for (int i = 0; i < tx.Length; i++)
        {
            Assert.Equal(tx[i], rx.Bit);
            rx.Shift();
        }
    }
}
