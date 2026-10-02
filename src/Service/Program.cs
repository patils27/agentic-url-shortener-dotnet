using AgenticUrlShortener.Service;

var app = ShortenerApp.CreateApp(args: args);
app.Run();

// Exposed for WebApplicationFactory in tests.
public partial class Program { }
