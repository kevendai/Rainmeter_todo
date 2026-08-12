using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;


internal static class CalendarRecurrenceProbe
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int passed;

    private static void Main()
    {
        Run("RRULE frequency generation", ProbeFrequencyRules);
        Run("RRULE interval and endings", ProbeRuleOptions);
        Run("weekly start alignment", ProbeWeeklyStartAlignment);
        Run("local recurrence expansion", ProbeLocalExpansion);
        Run("ICS recurrence parsing", ProbeParseIcs);
        Run("recurrence metadata parsing", ProbeRecurrenceMetadata);
        Run("ICS recurrence serialization", ProbeEventIcs);
        Run("series ICS preservation", ProbeSeriesIcsPreservation);
        Run("series RRULE replacement", ProbeSeriesRuleReplacement);
        Run("complex recurrence preservation", ProbeComplexRulePreserve);
        Run("local recurrence deletion", ProbeLocalDeletion);
        Console.WriteLine("PASS Calendar recurrence probe (" + passed.ToString(CultureInfo.InvariantCulture) + " groups)");
    }

    private static void Run(string name, Action probe)
    {
        try
        {
            probe();
            passed++;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + name + ": " + ex.Message);
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static MethodInfo Method(string name)
    {
        MethodInfo method = typeof(CalendarApp).GetMethods(StaticPrivate).FirstOrDefault(candidate => candidate.Name == name);
        if (method == null) throw new Exception("CalendarApp." + name + " was not found");
        return method;
    }

    private static object Invoke(string name, params object[] arguments)
    {
        try { return Method(name).Invoke(null, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }

    private static Type RecurrenceSpecType()
    {
        Type type = typeof(CalendarApp).GetNestedType("RecurrenceSpec", BindingFlags.NonPublic);
        if (type == null) throw new Exception("CalendarApp.RecurrenceSpec was not found");
        return type;
    }

    private static object Spec(string frequency, int interval, IEnumerable<int> weekdays, string endMode, DateTime until, int count)
    {
        Type type = RecurrenceSpecType();
        object value = Activator.CreateInstance(type, true);
        SetField(value, "Frequency", frequency);
        SetField(value, "Interval", interval);
        SetField(value, "Weekdays", weekdays == null ? new List<int>() : weekdays.ToList());
        SetField(value, "EndMode", endMode);
        SetField(value, "Until", until);
        SetField(value, "Count", count);
        return value;
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, InstanceFields);
        if (field == null) throw new Exception(target.GetType().Name + "." + name + " was not found");
        field.SetValue(target, value);
    }

    private static T Field<T>(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, InstanceFields);
        if (field == null) throw new Exception(target.GetType().Name + "." + name + " was not found");
        return (T)field.GetValue(target);
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute, int second)
    {
        DateTime local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static string Rule(object spec, DateTimeOffset start, bool allDay)
    {
        return (string)Invoke("BuildRecurrenceRule", spec, start, allDay);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception(message + "; expected <" + expected + ">, actual <" + actual + ">");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        List<T> expectedValues = expected.ToList(), actualValues = actual.ToList();
        if (!expectedValues.SequenceEqual(actualValues))
            throw new Exception(message + "; expected <" + String.Join(", ", expectedValues) + ">, actual <" + String.Join(", ", actualValues) + ">");
    }

    private static void ProbeFrequencyRules()
    {
        DateTimeOffset monday = Local(2024, 1, 1, 9, 30, 0);
        Equal("FREQ=DAILY", Rule(Spec("daily", 1, null, "never", monday.Date, 10), monday, false), "Daily RRULE changed");
        Equal("FREQ=WEEKLY;BYDAY=MO,WE,SU", Rule(Spec("weekly", 1, new[] { 7, 3, 1, 3 }, "never", monday.Date, 10), monday, false), "Weekly multi-day RRULE changed");

        DateTimeOffset monthEnd = Local(2024, 1, 31, 9, 30, 0);
        Equal("FREQ=MONTHLY;BYMONTHDAY=31", Rule(Spec("monthly", 1, null, "never", monthEnd.Date, 10), monthEnd, false), "Monthly RRULE changed");

        DateTimeOffset leapDay = Local(2024, 2, 29, 9, 30, 0);
        Equal("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29", Rule(Spec("yearly", 1, null, "never", leapDay.Date, 10), leapDay, false), "Yearly RRULE changed");
    }

    private static void ProbeRuleOptions()
    {
        DateTimeOffset start = Local(2024, 6, 1, 9, 30, 45);
        Equal("FREQ=DAILY;INTERVAL=3;COUNT=4", Rule(Spec("daily", 3, null, "count", start.Date, 4), start, false), "Interval/count RRULE changed");

        DateTime untilDate = new DateTime(2024, 7, 10);
        DateTime untilClock = new DateTime(untilDate.Year, untilDate.Month, untilDate.Day, start.Hour, start.Minute, start.Second, DateTimeKind.Unspecified);
        string expectedUtcUntil = new DateTimeOffset(untilClock, TimeZoneInfo.Local.GetUtcOffset(untilClock)).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        Equal("FREQ=DAILY;UNTIL=" + expectedUtcUntil, Rule(Spec("daily", 1, null, "until", untilDate, 10), start, false), "Timed UNTIL RRULE changed");
        Equal("FREQ=DAILY;UNTIL=20240710", Rule(Spec("daily", 1, null, "until", untilDate, 10), start, true), "All-day UNTIL RRULE changed");
    }

    private static void ProbeWeeklyStartAlignment()
    {
        object spec = Spec("weekly", 1, new[] { 1, 5 }, "never", DateTime.Today, 10);
        DateTimeOffset start = Local(2024, 1, 3, 9, 30, 0), end = start.AddHours(1);
        object[] arguments = { spec, start, end };
        Method("AlignRecurrenceStart").Invoke(null, arguments);
        Equal(Local(2024, 1, 5, 9, 30, 0), (DateTimeOffset)arguments[1], "Weekly start did not move to the first selected weekday");
        Equal(Local(2024, 1, 5, 10, 30, 0), (DateTimeOffset)arguments[2], "Weekly end did not retain the event duration");

        object monthly = Spec("monthly", 1, null, "never", DateTime.Today, 10);
        SetField(monthly, "MonthDay", 15);
        arguments = new object[] { monthly, Local(2024, 1, 20, 9, 30, 0), Local(2024, 1, 20, 10, 30, 0) };
        Method("AlignRecurrenceStart").Invoke(null, arguments);
        Equal(Local(2024, 2, 15, 9, 30, 0), (DateTimeOffset)arguments[1], "Monthly start did not move to the next configured day");
        Equal("FREQ=MONTHLY;BYMONTHDAY=15", Rule(monthly, (DateTimeOffset)arguments[1], false), "Monthly configured day was not serialized");

        object yearly = Spec("yearly", 1, null, "never", DateTime.Today, 10);
        SetField(yearly, "Month", 3); SetField(yearly, "MonthDay", 8);
        arguments = new object[] { yearly, Local(2024, 4, 1, 9, 30, 0), Local(2024, 4, 1, 10, 30, 0) };
        Method("AlignRecurrenceStart").Invoke(null, arguments);
        Equal(Local(2025, 3, 8, 9, 30, 0), (DateTimeOffset)arguments[1], "Yearly start did not move to the next configured date");
        Equal("FREQ=YEARLY;BYMONTH=3;BYMONTHDAY=8", Rule(yearly, (DateTimeOffset)arguments[1], false), "Yearly configured date was not serialized");
    }

    private static Dictionary<string, object> Master(string id, string uid, DateTimeOffset start, string rule, bool allDay)
    {
        return new Dictionary<string, object> {
            {"id", id}, {"occurrence_key", uid + "|master"}, {"uid", uid},
            {"recurrence_id", start.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)},
            {"title", "recurrence probe"}, {"start_at", start.ToString("o", CultureInfo.InvariantCulture)},
            {"end_at", start.AddHours(allDay ? 24 : 1).ToString("o", CultureInfo.InvariantCulture)},
            {"all_day", allDay}, {"url", ""}, {"location", ""}, {"description", ""},
            {"status", ""}, {"reminder_at", ""}, {"reminder_count", 0},
            {"reminders", new List<object>()}, {"custom_alarms", new List<object>()},
            {"rrule", rule}, {"recurring", true}, {"source", "local"}, {"calendar", "local"}
        };
    }

    private static List<Dictionary<string, object>> Expand(Dictionary<string, object> master)
    {
        object result = Invoke("ExpandLocalSeries", master);
        IEnumerable<Dictionary<string, object>> typed = result as IEnumerable<Dictionary<string, object>>;
        if (typed == null) throw new Exception("ExpandLocalSeries did not return event dictionaries");
        return typed.ToList();
    }

    private static List<string> Dates(IEnumerable<Dictionary<string, object>> events)
    {
        return events.Select(item => DateTimeOffset.Parse(Convert.ToString(item["start_at"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
    }

    private static void ProbeLocalExpansion()
    {
        Dictionary<string, object> monthEnd = Master("month-series", "month@example.test", Local(2024, 1, 31, 9, 0, 0), "FREQ=MONTHLY;BYMONTHDAY=31;COUNT=3", false);
        List<Dictionary<string, object>> first = Expand(monthEnd), second = Expand(monthEnd);
        SequenceEqual(new[] { "2024-01-31", "2024-03-31", "2024-05-31" }, Dates(first), "Month-end recurrence did not skip missing dates");
        SequenceEqual(first.Select(item => Convert.ToString(item["id"], CultureInfo.InvariantCulture)), second.Select(item => Convert.ToString(item["id"], CultureInfo.InvariantCulture)), "Expanded occurrence IDs were not stable");
        SequenceEqual(first.Select(item => Convert.ToString(item["occurrence_key"], CultureInfo.InvariantCulture)), second.Select(item => Convert.ToString(item["occurrence_key"], CultureInfo.InvariantCulture)), "Expanded occurrence keys were not stable");
        Equal(first.Count, first.Select(item => Convert.ToString(item["id"], CultureInfo.InvariantCulture)).Distinct().Count(), "Expanded occurrence IDs were not unique");
        Assert(first.All(item => Convert.ToString(item["series_id"], CultureInfo.InvariantCulture) == "month-series"), "Expanded occurrences lost their series ID");
        Assert(first.All(item => Convert.ToString(item["id"], CultureInfo.InvariantCulture).Length == 32), "Expanded occurrence ID format changed");

        Dictionary<string, object> leapDay = Master("leap-series", "leap@example.test", Local(2024, 2, 29, 8, 0, 0), "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29;COUNT=3", true);
        List<Dictionary<string, object>> leapOccurrences = Expand(leapDay);
        SequenceEqual(new[] { "2024-02-29", "2028-02-29", "2032-02-29" }, Dates(leapOccurrences), "Leap-day recurrence did not skip non-leap years");
        Assert(leapOccurrences.All(item => Convert.ToBoolean(item["all_day"], CultureInfo.InvariantCulture)), "Leap-day expansion lost all-day state");
    }

    private static List<Dictionary<string, object>> Parse(string ics)
    {
        object result = Invoke("ParseIcs", ics);
        List<Dictionary<string, object>> typed = result as List<Dictionary<string, object>>;
        if (typed == null) throw new Exception("ParseIcs did not return event dictionaries");
        return typed;
    }

    private static void ProbeParseIcs()
    {
        string ics =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
            "BEGIN:VEVENT\r\nUID:series@example.test\r\nDTSTART:20240101T010000Z\r\nDTEND:20240101T020000Z\r\nSUMMARY:Master\r\nRRULE:FREQ=WEEKLY;BYDAY=MO,WE\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:series@example.test\r\nRECURRENCE-ID:20240103T010000Z\r\nDTSTART:20240103T030000Z\r\nDTEND:20240103T040000Z\r\nSUMMARY:Moved instance\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:rdate@example.test\r\nDTSTART;VALUE=DATE:20240105\r\nDTEND;VALUE=DATE:20240106\r\nSUMMARY:RDATE master\r\nRDATE;VALUE=DATE:20240112\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n";

        List<Dictionary<string, object>> events = Parse(ics);
        Equal(3, events.Count, "ParseIcs recurrence fixture count changed");
        Dictionary<string, object> master = events.First(item => Convert.ToString(item["title"], CultureInfo.InvariantCulture) == "Master");
        Dictionary<string, object> moved = events.First(item => Convert.ToString(item["title"], CultureInfo.InvariantCulture) == "Moved instance");
        Dictionary<string, object> rdate = events.First(item => Convert.ToString(item["title"], CultureInfo.InvariantCulture) == "RDATE master");
        Assert(Convert.ToBoolean(master["recurring"], CultureInfo.InvariantCulture), "RRULE master was not marked recurring");
        Equal("FREQ=WEEKLY;BYDAY=MO,WE", Convert.ToString(master["rrule"], CultureInfo.InvariantCulture), "Parsed RRULE changed");
        Assert(Convert.ToBoolean(master["recurrence_preserve"], CultureInfo.InvariantCulture), "Series with an exception instance was not protected from recurrence simplification");
        Assert(Convert.ToBoolean(moved["recurring"], CultureInfo.InvariantCulture), "RECURRENCE-ID instance was not marked recurring");
        Equal("", Convert.ToString(moved["rrule"], CultureInfo.InvariantCulture), "RECURRENCE-ID instance unexpectedly gained an RRULE");
        Assert(Convert.ToString(moved["occurrence_key"], CultureInfo.InvariantCulture).Contains("2024-01-03T01:00:00"), "RECURRENCE-ID was not used for the occurrence key");
        Assert(Convert.ToBoolean(rdate["recurring"], CultureInfo.InvariantCulture), "RDATE master was not marked recurring");
        Assert(Convert.ToBoolean(rdate["recurrence_preserve"], CultureInfo.InvariantCulture), "RDATE master was not marked for rule preservation");
    }

    private static void ProbeEventIcs()
    {
        Dictionary<string, object> recurrence = Master("ics-series", "ics@example.test", Local(2024, 1, 1, 9, 0, 0), "FREQ=WEEKLY;BYDAY=MO,WE;COUNT=5", false);
        string serialized = (string)Invoke("EventIcs", recurrence);
        MatchCollection rules = Regex.Matches(serialized, @"(?im)^RRULE(?:;[^:]*)?:.*$");
        Equal(1, rules.Count, "EventIcs did not emit exactly one RRULE property");
        Equal("RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=5", rules[0].Value.TrimEnd('\r'), "EventIcs changed the RRULE value");
    }

    private static void ProbeRecurrenceMetadata()
    {
        string ics =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
            "BEGIN:VEVENT\r\nUID:timed@example.test\r\nDTSTART:20240101T010000Z\r\nDTEND:20240101T023000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEXDATE:20240102T010000Z\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:allday@example.test\r\nDTSTART;VALUE=DATE:20240229\r\nDTEND;VALUE=DATE:20240301\r\nRRULE:FREQ=YEARLY\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n";
        IDictionary metadata = Invoke("ParseRecurrenceMetadata", ics) as IDictionary;
        Assert(metadata != null && metadata.Count == 2, "ParseRecurrenceMetadata did not return both series masters");

        object timed = metadata["timed@example.test"];
        Assert(timed != null, "Timed series metadata was missing");
        DateTimeOffset? timedStart = Field<DateTimeOffset?>(timed, "Start"), timedEnd = Field<DateTimeOffset?>(timed, "End");
        Assert(timedStart.HasValue && timedEnd.HasValue, "Timed series start/end metadata was missing");
        Equal(new DateTimeOffset(2024, 1, 1, 1, 0, 0, TimeSpan.Zero), timedStart.Value.ToUniversalTime(), "Timed series start metadata changed");
        Equal(new DateTimeOffset(2024, 1, 1, 2, 30, 0, TimeSpan.Zero), timedEnd.Value.ToUniversalTime(), "Timed series end metadata changed");
        Assert(!Field<bool>(timed, "AllDay"), "Timed series was marked all-day in metadata");
        Assert(Field<bool>(timed, "Preserve"), "EXDATE series metadata was not protected from recurrence simplification");

        object allDay = metadata["allday@example.test"];
        Assert(allDay != null, "All-day series metadata was missing");
        DateTimeOffset? allDayStart = Field<DateTimeOffset?>(allDay, "Start"), allDayEnd = Field<DateTimeOffset?>(allDay, "End");
        Assert(allDayStart.HasValue && allDayEnd.HasValue, "All-day series start/end metadata was missing");
        Equal("2024-02-29", allDayStart.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "All-day series start metadata changed");
        Equal("2024-03-01", allDayEnd.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "All-day series end metadata changed");
        Assert(Field<bool>(allDay, "AllDay"), "All-day series flag was lost in metadata");
    }

    private static string SeriesFixture(out string exceptionBlock)
    {
        exceptionBlock =
            "BEGIN:VEVENT\r\n" +
            "UID:update@example.test\r\n" +
            "RECURRENCE-ID;TZID=China Standard Time:20240108T090000\r\n" +
            "DTSTART;TZID=China Standard Time:20240108T110000\r\n" +
            "DTEND;TZID=China Standard Time:20240108T120000\r\n" +
            "SUMMARY:Moved exception\r\n" +
            "X-PROBE:keep-me\r\n" +
            "END:VEVENT\r\n";
        return
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" +
            "BEGIN:VEVENT\r\n" +
            "UID:update@example.test\r\n" +
            "DTSTAMP:20231201T000000Z\r\n" +
            "DTSTART;TZID=China Standard Time:20240101T090000\r\n" +
            "DTEND;TZID=China Standard Time:20240101T100000\r\n" +
            "SUMMARY:Original master\r\n" +
            "RRULE:FREQ=MONTHLY;BYDAY=1MO;BYSETPOS=1\r\n" +
            "EXDATE;TZID=China Standard Time:20240205T090000\r\n" +
            "X-MASTER-PROP:keep-master\r\n" +
            "END:VEVENT\r\n" +
            exceptionBlock +
            "END:VCALENDAR\r\n";
    }

    private static Dictionary<string, object> SeriesUpdate(bool recurrenceChanged, string rule)
    {
        Dictionary<string, object> update = Master("update-series", "update@example.test", Local(2030, 6, 15, 15, 45, 0), rule, false);
        update["title"] = "Edited master";
        update["time_changed"] = false;
        update["rrule_changed"] = recurrenceChanged;
        return update;
    }

    private static void ProbeSeriesIcsPreservation()
    {
        string exceptionBlock;
        string raw = SeriesFixture(out exceptionBlock);
        string updated = (string)Invoke("UpdateSeriesIcs", raw, SeriesUpdate(false, "FREQ=DAILY;COUNT=9"));
        Assert(updated.Contains("DTSTART;TZID=China Standard Time:20240101T090000\r\n"), "time_changed=false rewrote the master DTSTART");
        Assert(updated.Contains("DTEND;TZID=China Standard Time:20240101T100000\r\n"), "time_changed=false rewrote the master DTEND");
        Assert(!updated.Contains("DTSTART:20300615"), "time_changed=false inserted the edited occurrence time");
        Equal(1, Regex.Matches(updated, @"(?im)^RRULE(?:;[^:]*)?:.*$").Count, "Unchanged complex series did not retain exactly one RRULE");
        Assert(updated.Contains("RRULE:FREQ=MONTHLY;BYDAY=1MO;BYSETPOS=1\r\n"), "rrule_changed=false rewrote the complex RRULE");
        Assert(updated.Contains("EXDATE;TZID=China Standard Time:20240205T090000\r\n"), "Series update removed EXDATE");
        Assert(updated.Contains(exceptionBlock), "Series update changed the exception VEVENT");
    }

    private static void ProbeSeriesRuleReplacement()
    {
        string exceptionBlock;
        string raw = SeriesFixture(out exceptionBlock);
        const string replacement = "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6";
        string updated = (string)Invoke("UpdateSeriesIcs", raw, SeriesUpdate(true, replacement));
        MatchCollection rules = Regex.Matches(updated, @"(?im)^RRULE(?:;[^:]*)?:.*$");
        Equal(1, rules.Count, "rrule_changed=true did not leave exactly one RRULE");
        Equal("RRULE:" + replacement, rules[0].Value.TrimEnd('\r'), "rrule_changed=true did not install the replacement RRULE");
        Assert(!updated.Contains("FREQ=MONTHLY;BYDAY=1MO;BYSETPOS=1"), "rrule_changed=true retained the old complex RRULE");
        Assert(updated.Contains("EXDATE;TZID=China Standard Time:20240205T090000\r\n"), "RRULE replacement removed EXDATE");
        Assert(updated.Contains(exceptionBlock), "RRULE replacement changed the exception VEVENT");
    }

    private static void ProbeComplexRulePreserve()
    {
        Dictionary<string, object> complex = Master("complex-series", "complex@example.test", Local(2024, 1, 1, 9, 0, 0), "FREQ=MONTHLY;BYDAY=1MO;BYSETPOS=1", false);
        object spec = Invoke("RecurrenceFromEvent", complex);
        Assert(Field<bool>(spec, "Preserve"), "Complex RRULE was simplified instead of preserved");
        Equal("preserve", Field<string>(spec, "Frequency"), "Complex RRULE did not use preserve mode");
        Equal("", Rule(spec, Local(2024, 1, 1, 9, 0, 0), false), "Preserved recurrence unexpectedly generated a replacement RRULE");
    }

    private static void ProbeLocalDeletion()
    {
        Dictionary<string, object> state = (Dictionary<string, object>)Invoke("NewState");
        Dictionary<string, object> master = Master("delete-series", "delete@example.test", Local(2024, 1, 31, 9, 0, 0), "FREQ=MONTHLY;BYMONTHDAY=31;COUNT=3", false);
        Invoke("SaveLocalEvent", master, state);
        Dictionary<string, object> occurrence = Expand(master)[1];

        Invoke("DeleteLocalEvent", occurrence, state, "once");
        Invoke("DeleteLocalEvent", occurrence, state, "once");
        List<Dictionary<string, object>> hidden = (List<Dictionary<string, object>>)state["hidden_events"];
        List<Dictionary<string, object>> local = (List<Dictionary<string, object>>)state["local_events"];
        Equal(1, hidden.Count, "Deleting one local occurrence was not deduplicated");
        Equal(Convert.ToString(occurrence["occurrence_key"], CultureInfo.InvariantCulture), Convert.ToString(hidden[0]["occurrence_key"], CultureInfo.InvariantCulture), "Hidden local occurrence key changed");
        Equal(1, local.Count, "Deleting one local occurrence removed the series master");
        Equal("delete-series", Convert.ToString(local[0]["id"], CultureInfo.InvariantCulture), "Deleting one local occurrence replaced the series master");

        Invoke("DeleteLocalEvent", occurrence, state, "series");
        local = (List<Dictionary<string, object>>)state["local_events"];
        Equal(0, local.Count, "Deleting a local series did not remove its master");
    }
}
