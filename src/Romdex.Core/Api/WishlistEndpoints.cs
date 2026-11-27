using Romdex.Client;
using Romdex.Core.Access;
using Romdex.Core.Wishlists;

namespace Romdex.Core.Api;

public static class WishlistEndpoints
{
    public static RouteGroupBuilder MapWishlistEndpoints(this RouteGroupBuilder api)
    {
        var wishlist = api.MapGroup("/wishlist").WithTags("Wishlist").RequireScope(Scopes.Read);

        wishlist.MapGet("", async (HttpContext http, WishlistService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.GetAsync(user, ct)) : NoOwner())
            .WithName("GetWishlist")
            .WithSummary("Your wishlist, newest first: games you'd like, less what the library has now, and whether anything can fetch them.")
            .Produces<Wishlist>();

        wishlist.MapPost("", async (AddToWishlist body, HttpContext http, WishlistService service, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                return await service.AddAsync(user, body, ct) is { } problem
                    ? Results.ValidationProblem(new Dictionary<string, string[]> { ["titleId"] = [problem] })
                    : Results.NoContent();
            })
            .WithName("AddToWishlist")
            .WithSummary("Put a game, or one of its dumps, on your wishlist. Adding one already there changes only which dump.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        // Romdex's own: Melodex has no list of what an artist's missing to wish for all at once.
        api.MapPost("/systems/{id:guid}/wishlist", async (Guid id, HttpContext http, WishlistService service, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                return await service.AddMissingAsync(user, id, ct) is { } added ? Results.Ok(new AddedToWishlist(added)) : Results.NotFound();
            })
            .RequireScope(Scopes.Read).WithTags("Wishlist").WithName("WishForMissing")
            .WithSummary("Put every game a system is missing on your wishlist, as its Missing list has them. Added says how many weren't on it already.")
            .Produces<AddedToWishlist>().Produces(StatusCodes.Status404NotFound);

        wishlist.MapPost("/get", async (HttpContext http, Romdex.Core.Library.ScheduledTasks scheduled, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(new AskedAgain(await scheduled.GetWishlistsAsync(user, ct))) : NoOwner())
            .RequireScope(Scopes.Download).WithName("GetWishlistItems")
            .WithSummary("Ask for everything on your wishlist, as Get does for each. Those being fetched already are left as they are; Asked says how many "
                         + "games were asked for.")
            .Produces<AskedAgain>();

        wishlist.MapDelete("/{titleId:guid}", async (Guid titleId, HttpContext http, WishlistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.RemoveAsync(user, titleId, ct) ? Results.NoContent() : Results.NotFound() : NoOwner())
            .WithName("RemoveFromWishlist")
            .WithSummary("Take a game off your wishlist, by its title ID.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        return api;
    }

    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("A wishlist belongs to a user, and this key doesn't: make a new one.", statusCode: StatusCodes.Status403Forbidden, title: "No user");
}
