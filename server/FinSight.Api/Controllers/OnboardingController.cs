using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Api.Controllers;

[ApiController]
[Route("api/onboarding")]
public sealed class OnboardingController(FinSightDbContext db, TimeProvider time) : ControllerBase
{
    /// <summary>Marks first-run onboarding as done. Idempotent: the first completion time is kept.</summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete(CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleAsync(cancellationToken);
        if (user.OnboardingCompletedAt is null)
        {
            user.OnboardingCompletedAt = time.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }
}
