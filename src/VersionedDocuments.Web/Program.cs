using Microsoft.EntityFrameworkCore;
using VersionedDocuments.Core;
using VersionedDocuments.Web;
using VersionedDocuments.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<DocsDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Docs")));

var dataRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var options = builder.Configuration.GetSection("Documents").Get<DocumentOptions>() ?? new DocumentOptions();
options.TempDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "tmp")).FullName;
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IBlobStore>(new FileSystemBlobStore(Path.Combine(dataRoot, "blobs")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DocumentService>();

// Stand-in for SSO: one per circuit, chosen from the top bar.
builder.Services.AddScoped<CurrentUser>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();

// Downloads go through a plain endpoint, not the Blazor circuit, so large files stream
// straight from disk and the browser handles resume (range requests).
app.MapGet("/documents/{id:int}/versions/{version:int}", async (int id, int version, DocumentService docs, CancellationToken ct) =>
{
    var opened = await docs.OpenAsync(id, version, ct);
    if (opened is null) return Results.NotFound();
    var (v, content) = opened.Value;
    // Always an attachment: the browser saves the file instead of rendering it on our origin.
    return Results.File(content, ContentInspector.MimeType(v.Kind), v.FileName, enableRangeProcessing: true);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

if (args.Contains("--reset") || app.Environment.IsDevelopment())
    await DemoSeed.RunAsync(app.Services, reset: args.Contains("--reset"));

app.Run();
