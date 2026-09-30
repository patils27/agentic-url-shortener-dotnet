using AgenticUrlShortener.Service;

var app = ShortenerApp.CreateApp();
app.Run();

// Exposed for WebApplicationFactory in tests.
public partial class Program { }
