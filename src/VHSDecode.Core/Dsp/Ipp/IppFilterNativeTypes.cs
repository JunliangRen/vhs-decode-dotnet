using System.Runtime.InteropServices;

namespace VHSDecode.Core.Dsp.Ipp;

[StructLayout(LayoutKind.Sequential, Pack = sizeof(double))]
internal readonly struct IppSos64Section(SosSection section)
{
    internal readonly double B0 = section.B0;
    internal readonly double B1 = section.B1;
    internal readonly double B2 = section.B2;
    internal readonly double A0 = section.A0;
    internal readonly double A1 = section.A1;
    internal readonly double A2 = section.A2;
}
