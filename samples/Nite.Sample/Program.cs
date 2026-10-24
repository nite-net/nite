using Nite;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddNite();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// In Development this serves /_nite (compiled TS/JSX, pre-bundled packages, live reload).
// In Production it does nothing; `nite build` output in wwwroot/_nite is served as static assets.
app.UseNite();

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
