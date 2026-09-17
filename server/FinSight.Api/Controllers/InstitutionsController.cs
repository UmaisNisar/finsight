using FinSight.Api.Contracts;
using FinSight.Core.Statements;
using Microsoft.AspNetCore.Mvc;

namespace FinSight.Api.Controllers;

[ApiController]
[Route("api/institutions")]
public sealed class InstitutionsController : ControllerBase
{
    private static readonly IReadOnlyList<InstitutionDto> Institutions = KnownInstitutions.All
        .DistinctBy(i => i.Id)
        .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
        .Select(i => i.ToDto())
        .ToList();

    /// <summary>Banks with trusted sign-in links and download guidance, for users who download statements from their bank's website.</summary>
    [HttpGet]
    public IReadOnlyList<InstitutionDto> List() => Institutions;
}
