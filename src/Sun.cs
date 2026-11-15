namespace ConsoleDesktop;

/// <summary>Sun elevation (degrees) from the low-precision almanac formulas (good to ~0.5°, plenty for picking a day phase).</summary>
static class Sun
{
    public static double Elevation(DateTime utc, double lat, double lon)
    {
        double d = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays, rad = Math.PI / 180;
        double g = (357.529 + 0.98560028 * d) * rad, q = 280.459 + 0.98564736 * d;
        double L = (q + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * rad, e = (23.439 - 0.00000036 * d) * rad;
        double ra = Math.Atan2(Math.Cos(e) * Math.Sin(L), Math.Cos(L)) / rad, dec = Math.Asin(Math.Sin(e) * Math.Sin(L));
        double ha = ((18.697374558 + 24.06570982441908 * d) * 15 + lon - ra) * rad;
        return Math.Asin(Math.Sin(lat * rad) * Math.Sin(dec) + Math.Cos(lat * rad) * Math.Cos(dec) * Math.Cos(ha)) / rad;
    }

    /// <summary>Run with --selftest: Oslo noon at midsummer is about 53.5°, at midwinter about 6.6°, midnight in June stays below the horizon.</summary>
    public static bool SelfTest()
    {
        bool near(double a, double b) => Math.Abs(a - b) < 1.0;
        var ok = near(Elevation(new DateTime(2026, 6, 21, 11, 15, 0, DateTimeKind.Utc), 59.91, 10.75), 53.5)
              && near(Elevation(new DateTime(2026, 12, 21, 11, 15, 0, DateTimeKind.Utc), 59.91, 10.75), 6.6)
              && Elevation(new DateTime(2026, 6, 21, 23, 15, 0, DateTimeKind.Utc), 59.91, 10.75) is < 0 and > -8;
        return ok;
    }
}
