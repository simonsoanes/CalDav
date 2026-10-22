using Nullify.CalDav.Formats;

namespace Nullify.CalDav.Tests;

[TestClass]
public sealed class LegacyFormatTests
{
    [TestMethod]
    public void VCard21WithQuotedPrintableConvertsTo30()
    {
        const string v21 = "BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;J=C3=BCrgen;;;\r\n" +
                           "TEL;CELL;VOICE:+49 170 1234567\r\nNOTE;ENCODING=QUOTED-PRINTABLE:Line one=0D=0A=\r\nLine two, with comma\r\nEND:VCARD\r\n";
        var v30 = LegacyFormats.VCardTo30(v21);
        var card = ContentReader.Parse(v30).Single();
        Assert.AreEqual("3.0", card.Value("VERSION"));
        Assert.AreEqual("Müller;Jürgen;;;", card.Value("N"));
        Assert.AreEqual("Jürgen Müller", card.Value("FN"));
        CollectionAssert.AreEqual(new[] { "CELL", "VOICE" }, card.Property("TEL")!.Parameters.Single(p => p.Name == "TYPE").Values);
        Assert.AreEqual("Line one\nLine two, with comma", card.Property("NOTE")!.TextValue);
    }

    [TestMethod]
    public void VCard30RoundTripsThrough21()
    {
        const string v30 = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:abc\r\nFN:Zoë Example\r\nN:Example;Zoë;;;\r\nEMAIL;TYPE=WORK,INTERNET:zoe@example.com\r\nEND:VCARD\r\n";
        var v21 = LegacyFormats.VCardTo21(v30);
        StringAssert.Contains(v21, "VERSION:2.1");
        StringAssert.Contains(v21, "EMAIL;WORK;INTERNET:zoe@example.com");
        StringAssert.Contains(v21, "ENCODING=QUOTED-PRINTABLE");
        var back = ContentReader.Parse(LegacyFormats.VCardTo30(v21)).Single();
        Assert.AreEqual("Zoë Example", back.Property("FN")!.TextValue);
        Assert.AreEqual("abc", back.Value("UID"));
    }

    [TestMethod]
    public void VCalendar10EventConvertsTo20()
    {
        const string v10 = """
            BEGIN:VCALENDAR
            VERSION:1.0
            BEGIN:VEVENT
            SUMMARY:Team meeting
            DTSTART:20260105T090000Z
            DTEND:20260105T100000Z
            RRULE:W1 MO #5
            DALARM:20260105T084500Z;;;
            CATEGORIES:WORK;MEETING
            END:VEVENT
            END:VCALENDAR
            """;
        var ical = LegacyFormats.VCalendarTo20(v10, null, out var type);
        Assert.AreEqual("VEVENT", type);
        var info = CalendarObjectInfo.Analyze(ical);
        var ev = info.Root.Children.Single(c => c.Name == "VEVENT");
        Assert.AreEqual("FREQ=WEEKLY;BYDAY=MO;COUNT=5", ev.Value("RRULE"));
        Assert.AreEqual("-PT15M", ev.ChildrenNamed("VALARM").Single().Value("TRIGGER"));
        Assert.AreEqual("WORK,MEETING", ev.Value("CATEGORIES"));
        Assert.IsFalse(string.IsNullOrEmpty(info.Uid));
    }

    [TestMethod]
    public void AllDayVCalendarBecomesDateValues()
    {
        const string v10 = "BEGIN:VCALENDAR\r\nVERSION:1.0\r\nBEGIN:VEVENT\r\nSUMMARY:Holiday\r\nDTSTART:20260710T000000\r\nDTEND:20260710T235900\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var ev = CalendarObjectInfo.Analyze(LegacyFormats.VCalendarTo20(v10, null, out _)).Root.Children.Single();
        Assert.AreEqual("20260710", ev.Value("DTSTART"));
        Assert.AreEqual("20260711", ev.Value("DTEND"));
        Assert.AreEqual("DATE", ev.Property("DTSTART")!.GetParameter("VALUE"));
    }

    [TestMethod]
    public void ICalendarConvertsTo10WithUtcTimesAndRule()
    {
        const string ical = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//test//EN
            BEGIN:VTODO
            UID:todo-1
            DTSTAMP:20260101T000000Z
            SUMMARY:Pay rent
            DUE;TZID=Europe/London:20260701T120000
            RRULE:FREQ=MONTHLY;BYMONTHDAY=1;COUNT=12
            STATUS:NEEDS-ACTION
            END:VTODO
            END:VCALENDAR
            """;
        var v10 = LegacyFormats.VCalendarTo10(ical);
        StringAssert.Contains(v10, "VERSION:1.0");
        StringAssert.Contains(v10, "DUE:20260701T110000Z");
        StringAssert.Contains(v10, "RRULE:MD1 1 #12");
        StringAssert.Contains(v10, "STATUS:NEEDS ACTION");
    }

    [TestMethod]
    [DataRow("D2 #10", "FREQ=DAILY;INTERVAL=2;COUNT=10")]
    [DataRow("MP1 1+ MO 1- FR #0", "FREQ=MONTHLY;BYDAY=1MO,-1FR")]
    [DataRow("MD1 15 LD 20261231T000000Z", "FREQ=MONTHLY;BYMONTHDAY=15,-1;UNTIL=20261231T000000Z")]
    [DataRow("YM1 6 #0", "FREQ=YEARLY;BYMONTH=6")]
    public void RecurrenceGrammarConversion(string v10, string expected) =>
        Assert.AreEqual(expected, LegacyFormats.Vcal10RuleTo20(v10));

    [TestMethod]
    public void NotesConvertToJournal()
    {
        var ical = LegacyFormats.NoteToJournal("Shopping\nMilk, eggs", "text/plain", null);
        var j = CalendarObjectInfo.Analyze(ical).Root.Children.Single();
        Assert.AreEqual("VJOURNAL", j.Name);
        Assert.AreEqual("Shopping", j.Property("SUMMARY")!.TextValue);
        Assert.AreEqual("Shopping\nMilk, eggs", LegacyFormats.JournalToText(ical));
        StringAssert.Contains(LegacyFormats.JournalToVNote(ical), "BEGIN:VNOTE");
    }
}
