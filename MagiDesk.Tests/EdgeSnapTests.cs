using MagiDesk.Config;
using MagiDesk.Features.EdgeSnap;
using MagiDesk.Native;

namespace MagiDesk.Tests;

/// <summary>Audit actual production math against option semantics; no hooks or input.</summary>
internal static class EdgeSnapTests
{
    private static NativeMethods.RECT R(int x, int y, int w = 100, int h = 100)
        => new() { Left = x, Top = y, Right = x + w, Bottom = y + h };

    public static int Run()
    {
        int total = 0, failed = 0;
        void Test(string name, Action body)
        {
            total++;
            try { body(); Console.WriteLine($"PASS {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
        }
        void At(NativeMethods.RECT actual, int x, int y, int w = 100, int h = 100)
        {
            if (actual.Left != x || actual.Top != y || actual.Width != w || actual.Height != h)
                throw new Exception($"expected ({x},{y},{w},{h}), actual ({actual.Left},{actual.Top},{actual.Width},{actual.Height})");
        }
        AppConfig Config(bool monitor = false, bool edges = false, bool centers = false, int band = 12)
            => new() { EdgeSnapToMonitorEdges = monitor, EdgeSnapToWindowEdges = edges,
                EdgeSnapToWindowCenters = centers, EdgeSnapBand = band };
        var area = R(0, 0, 1000, 800);
        NativeMethods.RECT Calc(NativeMethods.RECT p, AppConfig c, params NativeMethods.RECT[] windows)
            => EdgeSnapEngine.Calculate(p, c, Array.Empty<NativeMethods.RECT>(), windows);

        Test("monitor left/top", () => At(EdgeSnapEngine.Calculate(R(8, 9), Config(monitor:true), [area], []), 0, 0));
        Test("monitor right/bottom", () => At(EdgeSnapEngine.Calculate(R(895, 693), Config(monitor:true), [area], []), 900, 700));
        Test("inclusive distance boundary", () => At(Calc(R(88, 300), Config(edges:true), R(200, 300)), 100, 300));
        Test("outside distance boundary", () => At(Calc(R(87, 300), Config(edges:true), R(200, 300)), 87, 300));
        Test("zero band disables calculation", () => At(Calc(R(95, 300), Config(edges:true,band:0), R(200, 300)), 95, 300));
        Test("negative band disables calculation", () => At(Calc(R(95, 300), Config(edges:true,band:-1), R(200, 300)), 95, 300));
        Test("abut right side", () => At(Calc(R(305, 300), Config(edges:true), R(200, 300)), 300, 300));
        Test("abut bottom side", () => At(Calc(R(200, 405), Config(edges:true), R(200, 300)), 200, 400));
        Test("matching edges", () => At(Calc(R(205, 305), Config(edges:true), R(200, 300)), 200, 300));
        Test("center alignment", () => At(Calc(R(205, 300), Config(centers:true), R(180, 300, 140, 100)), 200, 300));
        Test("nearest target wins", () => At(Calc(R(95, 300), Config(edges:true), R(203, 300), R(197, 300)), 97, 300));
        Test("all window targets off", () => At(Calc(R(95, 300), Config(), R(200, 300)), 95, 300));
        Test("negative monitor coordinates", () => At(EdgeSnapEngine.Calculate(R(-995, -795), Config(monitor:true), [R(-1000,-800,1000,800)], []), -1000, -800));
        Test("DWM insets preserve outer size", () => At(EdgeSnapEngine.Calculate(R(-5, 100, 114, 110), Config(monitor:true), [area], [], 7, 0, 7, 7), -7, 100, 114, 110));
        Test("option combinations only enable their own window targets", () =>
        {
            for (int mask = 0; mask < 8; mask++)
            {
                var c = Config((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
                At(Calc(R(95, 300), c, R(200, 300)), (mask & 2) != 0 ? 100 : 95, 300);
            }
        });
        // Regressions discovered by the original audit.
        Test("turning monitor target off ignores existing snapshot", () => At(EdgeSnapEngine.Calculate(R(8, 100), Config(), [area], []), 8, 100));
        Test("abut must not attract a vertically distant window", () => At(Calc(R(95, 300), Config(edges:true), R(200, 2000)), 95, 300));
        Test("adjacent alignment must not attract distant windows", () => At(Calc(R(205, 300), Config(edges:true), R(200, 2000)), 205, 300));
        Test("other monitor edge must not attract outside its span", () => At(EdgeSnapEngine.Calculate(R(8, 2000), Config(monitor:true), [area], []), 8, 2000));
        Test("master switch off ignores all cached targets", () =>
        {
            var c = Config(true, true, true); c.EdgeSnapEnabled = false;
            At(EdgeSnapEngine.Calculate(R(8, 9), c, [area], [R(10,10)]), 8, 9);
        });
        Test("nearby stacked windows still align", () => At(Calc(R(205, 410), Config(centers:true), R(200,300)), 200,410));
        Test("nearby side-by-side windows still align", () => At(Calc(R(310, 305), Config(centers:true), R(200,300)), 310,300));
        Test("perpendicular gap exactly at band", () => At(Calc(R(95, 300), Config(edges:true), R(200,412)), 100,312));
        Test("perpendicular gap beyond band", () => At(Calc(R(95, 300), Config(edges:true), R(200,413)), 95,300));
        Test("distant horizontal window cannot attract vertically", () => At(Calc(R(2000,205), Config(edges:true), R(200,200)), 2000,205));
        Test("distant horizontal monitor cannot attract vertically", () => At(EdgeSnapEngine.Calculate(R(2000,8), Config(monitor:true), [area], []), 2000,8));
        Test("monitor switch can re-enable existing snapshot", () =>
        {
            var c = Config(); At(EdgeSnapEngine.Calculate(R(8,100), c, [area], []),8,100);
            c.EdgeSnapToMonitorEdges = true; At(EdgeSnapEngine.Calculate(R(8,100), c, [area], []),0,100);
        });
        Test("window switches re-evaluated against same snapshot", () =>
        {
            var c = Config(edges:true); var p = R(95,300); var targets = new[] { R(200,300) };
            At(Calc(p,c,targets),100,300); c.EdgeSnapToWindowEdges = false;
            At(Calc(p,c,targets),95,300); c.EdgeSnapToWindowEdges = true;
            At(Calc(p,c,targets),100,300);
        });
        Console.WriteLine($"Edge snap audit: {total - failed}/{total} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
