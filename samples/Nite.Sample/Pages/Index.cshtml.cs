using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Nite.Sample.Pages;

public class IndexModel : PageModel
{
    public record Todo(int Id, string Title, bool Done);

    // Stand-in for data from a database; it reaches the island as props.
    public IReadOnlyList<Todo> Todos { get; } =
    [
        new(1, "Render the page on the server", true),
        new(2, "Hydrate only the interactive bits", false),
        new(3, "Skip installing Node.js", false),
    ];

    public void OnGet()
    {
    }
}
