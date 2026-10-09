using System.ComponentModel.DataAnnotations;
namespace Erp.Modules.Crm.Pages;

public sealed class LeadForm
{
    [Required, StringLength(200)] public string? Title { get; set; }
    public Guid? ContactId { get; set; }
    [EmailAddress, StringLength(254)] public string? Email { get; set; }
    [StringLength(50)] public string? Phone { get; set; }
    [StringLength(100)] public string? Source { get; set; }
    [Range(typeof(decimal), "0", "99999999999999")] public decimal ExpectedRevenue { get; set; }
    [Range(0, 100)] public int Probability { get; set; }
    public Guid? AssignedUserId { get; set; }
    public DateOnly? PlannedCloseDate { get; set; }
    [StringLength(4000)] public string? Notes { get; set; }
    public string? ConcurrencyStamp { get; set; }
    public LeadRequest ToRequest() => new(Title, ContactId, Email, Phone, Source, ExpectedRevenue, Probability, AssignedUserId, PlannedCloseDate, Notes, ConcurrencyStamp);
    public static LeadForm From(LeadDto lead) => new() { Title = lead.Title, ContactId = lead.ContactId, Email = lead.Email, Phone = lead.Phone, Source = lead.Source, ExpectedRevenue = lead.ExpectedRevenue, Probability = lead.Probability, AssignedUserId = lead.AssignedUserId, PlannedCloseDate = lead.PlannedCloseDate, Notes = lead.Notes, ConcurrencyStamp = lead.ConcurrencyStamp };
}
