using Nullify.CalDav.Formats;

namespace Nullify.CalDav.Tests;

[TestClass]
public sealed class RecurrenceTests
{
    private static List<DateTime> Expand(string rrule, DateTime start, int take = 50)
    {
        Assert.IsTrue(RecurrenceRule.TryParse(rrule, out var rule));
        return rule.Enumerate(start).Take(take).ToList();
    }

    [TestMethod]
    public void WeeklyByDayWithCount()
    {
        var r = Expand("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=5", new DateTime(2026, 1, 5, 9, 0, 0));
        CollectionAssert.AreEqual(new[]
        {
            new DateTime(2026, 1, 5, 9, 0, 0), new DateTime(2026, 1, 7, 9, 0, 0), new DateTime(2026, 1, 12, 9, 0, 0),
            new DateTime(2026, 1, 14, 9, 0, 0), new DateTime(2026, 1, 19, 9, 0, 0),
        }, r);
    }

    [TestMethod]
    public void MonthlyLastFriday()
    {
        var r = Expand("FREQ=MONTHLY;BYDAY=-1FR;COUNT=3", new DateTime(2026, 1, 30, 12, 0, 0));
        CollectionAssert.AreEqual(new[]
        {
            new DateTime(2026, 1, 30, 12, 0, 0), new DateTime(2026, 2, 27, 12, 0, 0), new DateTime(2026, 3, 27, 12, 0, 0),
        }, r);
    }

    [TestMethod]
    public void MonthlyThirtyFirstSkipsShortMonths()
    {
        var r = Expand("FREQ=MONTHLY;COUNT=4", new DateTime(2026, 1, 31));
        CollectionAssert.AreEqual(new[]
        {
            new DateTime(2026, 1, 31), new DateTime(2026, 3, 31), new DateTime(2026, 5, 31), new DateTime(2026, 7, 31),
        }, r);
    }

    [TestMethod]
    public void LastWeekdayOfMonthViaSetPos()
    {
        var r = Expand("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=3", new DateTime(2026, 1, 30));
        CollectionAssert.AreEqual(new[] { new DateTime(2026, 1, 30), new DateTime(2026, 2, 27), new DateTime(2026, 3, 31) }, r);
    }

    [TestMethod]
    public void YearlyBirthdayOnLeapDay()
    {
        var r = Expand("FREQ=YEARLY;COUNT=2", new DateTime(2024, 2, 29));
        CollectionAssert.AreEqual(new[] { new DateTime(2024, 2, 29), new DateTime(2028, 2, 29) }, r);
    }

    [TestMethod]
    public void YearlyThanksgiving()
    {
        var r = Expand("FREQ=YEARLY;BYMONTH=11;BYDAY=4TH;COUNT=3", new DateTime(2025, 11, 27));
        CollectionAssert.AreEqual(new[] { new DateTime(2025, 11, 27), new DateTime(2026, 11, 26), new DateTime(2027, 11, 25) }, r);
    }

    [TestMethod]
    public void DailyIntervalUntil()
    {
        Assert.IsTrue(RecurrenceRule.TryParse("FREQ=DAILY;INTERVAL=2;UNTIL=20260107", out var rule));
        var until = new DateTime(2026, 1, 7);
        var r = rule.Enumerate(new DateTime(2026, 1, 1), t => t.Date > until).ToList();
        CollectionAssert.AreEqual(new[] { new DateTime(2026, 1, 1), new DateTime(2026, 1, 3), new DateTime(2026, 1, 5), new DateTime(2026, 1, 7) }, r);
    }

    private const string London = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//test//EN
        BEGIN:VEVENT
        UID:dst@test
        DTSTAMP:20260101T000000Z
        DTSTART;TZID=Europe/London:20260323T090000
        DTEND;TZID=Europe/London:20260323T100000
        RRULE:FREQ=DAILY;COUNT=14
        EXDATE;TZID=Europe/London:20260325T090000
        END:VEVENT
        BEGIN:VEVENT
        UID:dst@test
        DTSTAMP:20260101T000000Z
        RECURRENCE-ID;TZID=Europe/London:20260324T090000
        DTSTART;TZID=Europe/London:20260324T150000
        DTEND;TZID=Europe/London:20260324T160000
        SUMMARY:Moved
        END:VEVENT
        END:VCALENDAR
        """;

    [TestMethod]
    public void TimeZoneDstExdateAndOverride()
    {
        var info = CalendarObjectInfo.Analyze(London);
        var analyzer = new CalendarAnalyzer(info.Root);
        var all = analyzer.GetInstances("VEVENT", DateTime.MinValue, DateTime.MaxValue).OrderBy(i => i.StartUtc).ToList();

        Assert.HasCount(13, all); // 14 minus one EXDATE (override replaces its instance)
        Assert.AreEqual(new DateTime(2026, 3, 23, 9, 0, 0), all[0].StartUtc); // GMT
        Assert.AreEqual(new DateTime(2026, 3, 24, 15, 0, 0), all[1].StartUtc); // override
        Assert.AreEqual("Moved", all[1].Source.Value("SUMMARY"));
        Assert.IsFalse(all.Any(i => i.StartUtc.Date == new DateTime(2026, 3, 25)), "EXDATE must be excluded");
        Assert.AreEqual(new DateTime(2026, 3, 30, 8, 0, 0), all.First(i => i.StartUtc.Date == new DateTime(2026, 3, 30)).StartUtc); // BST

        Assert.AreEqual(CalendarObjectInfo.ToUnix(new DateTime(2026, 3, 23, 9, 0, 0)), info.FirstOccurrence);
        Assert.AreEqual(CalendarObjectInfo.ToUnix(new DateTime(2026, 4, 5, 9, 0, 0)), info.LastOccurrence);
    }

    [TestMethod]
    public void UnboundedRuleHasNoLastOccurrence()
    {
        var info = CalendarObjectInfo.Analyze("""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//test//EN
            BEGIN:VEVENT
            UID:forever@test
            DTSTAMP:20260101T000000Z
            DTSTART;VALUE=DATE:20200101
            RRULE:FREQ=YEARLY
            END:VEVENT
            END:VCALENDAR
            """);
        Assert.IsNull(info.LastOccurrence);
        var analyzer = new CalendarAnalyzer(info.Root);
        var hits = analyzer.GetInstances("VEVENT", new DateTime(2030, 1, 1), new DateTime(2030, 1, 2));
        Assert.HasCount(1, hits);
        Assert.AreEqual(new DateTime(2030, 1, 2), hits[0].EndUtc);
    }

    [TestMethod]
    public void EmbeddedVTimeZoneIsUsedForUnknownIds()
    {
        var info = CalendarObjectInfo.Analyze("""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//test//EN
            BEGIN:VTIMEZONE
            TZID:Custom Eastern
            BEGIN:STANDARD
            DTSTART:16011104T020000
            RRULE:FREQ=YEARLY;BYDAY=1SU;BYMONTH=11
            TZOFFSETFROM:-0400
            TZOFFSETTO:-0500
            END:STANDARD
            BEGIN:DAYLIGHT
            DTSTART:16010311T020000
            RRULE:FREQ=YEARLY;BYDAY=2SU;BYMONTH=3
            TZOFFSETFROM:-0500
            TZOFFSETTO:-0400
            END:DAYLIGHT
            END:VTIMEZONE
            BEGIN:VEVENT
            UID:tz@test
            DTSTAMP:20260101T000000Z
            DTSTART;TZID=Custom Eastern:20260701T120000
            DTEND;TZID=Custom Eastern:20260701T130000
            END:VEVENT
            END:VCALENDAR
            """);
        Assert.AreEqual(CalendarObjectInfo.ToUnix(new DateTime(2026, 7, 1, 16, 0, 0)), info.FirstOccurrence);
    }

    [TestMethod]
    public void ContentLineParsingHandlesQuotedParametersAndFolding()
    {
        var roots = ContentReader.Parse("BEGIN:VCARD\r\nVERSION:4.0\r\nitem1.EMAIL;TYPE=\"work,pref\":a@b.c\r\nNOTE:line one\\nline\r\n  two\r\nEND:VCARD\r\n");
        var card = roots.Single();
        var email = card.Property("EMAIL")!;
        Assert.AreEqual("item1", email.Group);
        CollectionAssert.AreEqual(new[] { "work,pref" }, email.Parameters[0].Values);
        Assert.AreEqual("line one\nline two", card.Property("NOTE")!.TextValue);
    }
}
