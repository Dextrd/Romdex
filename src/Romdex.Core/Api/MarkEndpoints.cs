using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Access;
using Romdex.Core.Data;
using Romdex.Core.Marks;

namespace Romdex.Core.Api;

/// <summary>Each person's stars and ratings on games, as Melodex's are on songs, albums and artists.</summary>
public static class MarkEndpoints
{
    public static RouteGroupBuilder MapMarkEndpoints(this RouteGroupBuilder api)
    {
        var marks = api.MapGroup("/marks").WithTags("Stars and ratings").RequireScope(Scopes.Read);

        marks.MapGet("", async (HttpContext http, MarkService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.ListAsync(user, ct)) : NoOwner())
            .WithName("ListMarks")
            .WithSummary("Your stars and ratings on games, the latest first.")
            .Produces<IReadOnlyList<ItemMark>>();

        marks.MapPut("/{kind}/{id:guid}", async (string kind, Guid id, SetMark body, HttpContext http, MarkService service, RomdexDbContext db,
                CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                if (!Enum.TryParse<MarkKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Choose game."] });
                if (body.Rating is < 0 or > 5)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["rating"] = ["A rating is 1 to 5, or 0 to take it away."] });
                if (!await db.Titles.AnyAsync(t => t.Id == id, ct))
                    return Results.NotFound();
                return await service.SetAsync(user, parsed, id, body, ct) is { } mark ? Results.Ok(mark) : Results.NoContent();
            })
            .WithName("SetMark")
            .WithSummary("Star or unstar a game (by its title ID), or rate it 1 to 5 (0 takes the rating away). What's left out stays as it "
                         + "was. No content once it has neither a star nor a rating.")
            .Produces<ItemMark>().Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).ProducesValidationProblem();

        return api;
    }

    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("Stars and ratings belong to a user, and this key doesn't: make a new one.", statusCode: StatusCodes.Status403Forbidden, title: "No user");
}
