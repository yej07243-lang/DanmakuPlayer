using System.Globalization;
using System.IO;

namespace DanmakuPlayerWin.Playback;

/// <summary>校准映射的自检用例。不依赖 UI，<c>--check-calibration</c> 直接跑它。</summary>
public static class CalibrationChecks
{
    public static bool Run(Action<string> log)
    {
        var failures = 0;

        void Check(string name, double expected, double actual, double tolerance = 0.001)
        {
            var ok = Math.Abs(expected - actual) <= tolerance;
            if (!ok) failures++;
            log(string.Create(CultureInfo.InvariantCulture,
                $"{(ok ? "PASS" : "FAIL")}  {name}: expected {expected:0.###}, actual {actual:0.###}"));
        }

        void CheckTrue(string name, bool value)
        {
            if (!value) failures++;
            log($"{(value ? "PASS" : "FAIL")}  {name}");
        }

        // 1. 无锚点：恒等映射
        var identity = new Calibration();
        Check("no-anchal ToLocal identity", 123.4, identity.ToLocal(123.4));
        Check("no-anchal ToBili identity", 123.4, identity.ToBili(123.4));
        Check("no-anchal OffsetAt zero", 0, identity.OffsetAt(500));

        // 2. 单锚点：整段平移
        var single = new Calibration(null, new[] { new Anchor(600, 500) });
        Check("single ToLocal at anchor", 600, single.ToLocal(500));
        Check("single ToLocal after", 610, single.ToLocal(510));
        Check("single ToLocal before", 500, single.ToLocal(400));
        Check("single ToBili", 500, single.ToBili(600));

        // 3. 双锚点：段内 1:1，跨段阶跃
        var two = new Calibration(null, new[] { new Anchor(0, 0), new Anchor(400, 300) });
        Check("two first segment", 100, two.ToLocal(100));
        Check("two anchor point", 400, two.ToLocal(300));
        Check("two second segment", 450, two.ToLocal(350));
        Check("two tail", 1100, two.ToLocal(1000));
        Check("two negative", -5, two.ToLocal(-5));
        Check("two ToBili inverse", 350, two.ToBili(450));
        Check("two OffsetAt second", 100, two.OffsetAt(450));
        Check("two OffsetAt first", 0, two.OffsetAt(100));

        // 4. 乱序输入按 B 站时间排序后仍然正确
        var unsorted = new Calibration(null, new[] { new Anchor(400, 300), new Anchor(0, 0) });
        Check("unsorted normalizes", 450, unsorted.ToLocal(350));

        // 5. 校验：重复 B 站时间、非单调本地时间
        var duplicate = new Calibration(null, new[] { new Anchor(0, 0), new Anchor(10, 0) });
        CheckTrue("duplicate bili rejected", !duplicate.TryValidate(out _));

        var nonMonotonic = new Calibration(null, new[] { new Anchor(100, 0), new Anchor(50, 10) });
        CheckTrue("non-monotonic local rejected", !nonMonotonic.TryValidate(out _));

        CheckTrue("valid calibration accepted", two.TryValidate(out _));

        // 6. 时间解析
        CheckTrue("parse mm:ss.s", CalibrationStore.TryParseTime("12:34.5", out var t1) && Math.Abs(t1 - 754.5) < 0.001);
        CheckTrue("parse h:mm:ss", CalibrationStore.TryParseTime("1:02:03", out var t2) && Math.Abs(t2 - 3723) < 0.001);
        CheckTrue("parse seconds", CalibrationStore.TryParseTime("90", out var t3) && Math.Abs(t3 - 90) < 0.001);
        CheckTrue("reject garbage", !CalibrationStore.TryParseTime("abc", out _));

        log(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures} failures)");
        return failures == 0;
    }
}
