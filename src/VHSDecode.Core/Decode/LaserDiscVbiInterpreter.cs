namespace VHSDecode.Core.Decode;

public sealed record LaserDiscVbiInterpretation(
    int? FrameNumber,
    bool IsClv,
    bool IsEarlyClv,
    int? ClvMinutes,
    int? ClvSeconds,
    int? ClvFrameNumber,
    bool LeadIn,
    bool LeadOut);

public static class LaserDiscVbiInterpreter
{
    private const int LeadOutCode = 0x80EEEE;
    private const int LeadInCode = 0x88FFFF;

    public static LaserDiscVbiInterpretation Interpret(
        IEnumerable<int> lineCodes,
        int clvFramesPerSecond)
    {
        if (clvFramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clvFramesPerSecond));
        }

        bool leadIn = false;
        bool leadOut = false;
        bool isClv = false;
        bool earlyClv = false;
        int leadOutCount = 0;
        int? clvMinutes = null;
        int? clvSeconds = null;
        int? clvFrameNumber = null;

        foreach (int code in lineCodes)
        {
            if (code == LeadOutCode)
            {
                leadOutCount++;
                if (leadOutCount >= 2)
                {
                    leadOut = true;
                }
            }
            else if (code == LeadInCode)
            {
                leadIn = true;
            }
            else if ((code & 0xF0DD00) == 0xF0DD00)
            {
                try
                {
                    clvMinutes = DecodeBcd(code & 0xFF) + (DecodeBcd((code >> 16) & 0xF) * 60);
                    isClv = true;
                }
                catch (ArgumentException)
                {
                }
            }
            else if ((code & 0xF00000) == 0xF00000)
            {
                try
                {
                    return new LaserDiscVbiInterpretation(
                        FrameNumber: DecodeBcd(code & 0x7FFFF),
                        IsClv: false,
                        IsEarlyClv: false,
                        ClvMinutes: null,
                        ClvSeconds: null,
                        ClvFrameNumber: null,
                        LeadIn: leadIn,
                        LeadOut: leadOut);
                }
                catch (ArgumentException)
                {
                }
            }
            else if ((code & 0x80F000) == 0x80E000)
            {
                try
                {
                    int sec1s = DecodeBcd((code >> 8) & 0xF);
                    int sec10s = ((code >> 16) & 0xF) - 0xA;
                    if (sec10s < 0)
                    {
                        throw new ArgumentException("Digit 2 not in range A-F.");
                    }

                    clvFrameNumber = DecodeBcd(code & 0xFF);
                    clvSeconds = sec1s + (10 * sec10s);
                    isClv = true;
                }
                catch (ArgumentException)
                {
                }
            }

            if (clvMinutes.HasValue)
            {
                int minuteSeconds = clvMinutes.Value * 60;
                if (clvSeconds.HasValue && clvFrameNumber.HasValue)
                {
                    int frame = ((minuteSeconds + clvSeconds.Value) * clvFramesPerSecond) + clvFrameNumber.Value;
                    return new LaserDiscVbiInterpretation(
                        FrameNumber: frame,
                        IsClv: true,
                        IsEarlyClv: false,
                        ClvMinutes: clvMinutes,
                        ClvSeconds: clvSeconds,
                        ClvFrameNumber: clvFrameNumber,
                        LeadIn: leadIn,
                        LeadOut: leadOut);
                }

                earlyClv = true;
                return new LaserDiscVbiInterpretation(
                    FrameNumber: minuteSeconds,
                    IsClv: true,
                    IsEarlyClv: true,
                    ClvMinutes: clvMinutes,
                    ClvSeconds: null,
                    ClvFrameNumber: null,
                    LeadIn: leadIn,
                    LeadOut: leadOut);
            }
        }

        return new LaserDiscVbiInterpretation(
            FrameNumber: null,
            IsClv: isClv,
            IsEarlyClv: earlyClv,
            ClvMinutes: clvMinutes,
            ClvSeconds: clvSeconds,
            ClvFrameNumber: clvFrameNumber,
            LeadIn: leadIn,
            LeadOut: leadOut);
    }

    private static int DecodeBcd(int bcd)
    {
        int result = 0;
        int multiplier = 1;
        int value = bcd;
        while (value != 0)
        {
            int digit = value & 0xF;
            if (digit > 9)
            {
                throw new ArgumentException("Non-decimal BCD digit.", nameof(bcd));
            }

            result += digit * multiplier;
            multiplier *= 10;
            value >>= 4;
        }

        return result;
    }
}
