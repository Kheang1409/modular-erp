using System.ComponentModel.DataAnnotations;
namespace Erp.Modules.Crm.Pages;

public sealed class OpportunityForm
{
    [Required, StringLength(200)] public string? Title { get; set; }
    public Guid? ContactId { get; set; }
    public Guid StageId { get; set; }
    [Range(typeof(decimal), "0", "99999999999999")] public decimal ExpectedRevenue { get; set; }
    public DateOnly? PlannedCloseDate { get; set; }
    [StringLength(4000)] public string? Notes { get; set; }
    public string? ConcurrencyStamp { get; set; }
    public OpportunityRequest ToRequest() => new(Title, ContactId, StageId, ExpectedRevenue, null, PlannedCloseDate, Notes, ConcurrencyStamp);
    public static OpportunityForm From(OpportunityDto item) => new() { Title = item.Title, ContactId = item.ContactId, StageId = item.StageId, ExpectedRevenue = item.ExpectedRevenue, PlannedCloseDate = item.PlannedCloseDate, Notes = item.Notes, ConcurrencyStamp = item.ConcurrencyStamp };
}
