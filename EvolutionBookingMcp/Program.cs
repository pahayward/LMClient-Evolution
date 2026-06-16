using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(BookingTools).Assembly, null);

await builder.Build().RunAsync();

[McpServerToolType]
public class BookingTools
{
    // Pre-populate a few busy slots so availability checking gives realistic results
    private static readonly Dictionary<string, BookingRecord> _bookings = new()
    {
        [$"{DateTime.Today:yyyy-MM-dd} 09:00"] = new("Jane Smith", "jane@example.com"),
        [$"{DateTime.Today.AddDays(1):yyyy-MM-dd} 14:00"] = new("Bob Jones", "bob@example.com"),
        [$"{DateTime.Today.AddDays(2):yyyy-MM-dd} 10:00"] = new("Alice Brown", "alice@example.com"),
    };

    [McpServerTool]
    [Description("Check whether a one-hour consultation slot with the evolution expert is available on a given date and time.")]
    public static string CheckAvailability(
        [Description("Date in yyyy-MM-dd format, e.g. 2026-06-22")] string date,
        [Description("Start time in HH:mm 24-hour format, e.g. 14:00")] string time)
    {
        var key = $"{date} {time}";
        return _bookings.ContainsKey(key)
            ? $"Sorry, the slot on {date} at {time} is already booked. Please suggest a different date or time."
            : $"Great news — the slot on {date} at {time} is available!";
    }

    [McpServerTool]
    [Description("Book a one-hour consultation slot with the evolution expert for a specific person.")]
    public static string BookSlot(
        [Description("Date in yyyy-MM-dd format")] string date,
        [Description("Start time in HH:mm 24-hour format")] string time,
        [Description("Full name of the person booking")] string name,
        [Description("Email address for the booking confirmation")] string email)
    {
        var key = $"{date} {time}";
        if (_bookings.ContainsKey(key))
            return $"Sorry, the slot on {date} at {time} was just taken. Please choose a different time.";

        _bookings[key] = new(name, email);

        var endTime = TimeSpan.TryParse(time, out var ts)
            ? (ts + TimeSpan.FromHours(1)).ToString(@"hh\:mm")
            : "one hour later";

        return $"Booking confirmed! {name} is booked with the evolution expert on {date} from {time} to {endTime}. " +
               $"A confirmation would normally be sent to {email}.";
    }
}

record BookingRecord(string Name, string Email);
