namespace DynamicAi.Application.Models.DirectIntegration;

/// <summary>
/// السياق الذي يقوم النظام بحقنه متخطياً عملية البحث الذكي (RAG).
/// يعتمد على بنية منظمة (Relational Schema) تمنع الـ LLM من الهلوسة.
/// </summary>
public class ShippedPageContext
{
    public int PageId { get; set; }
    public string PageName { get; set; } = string.Empty;

    // 1. معلومات الـ Master
    public PageMasterSetup MasterSetup { get; set; } = new();

    // 2. معلومات الـ Details (في حال وجود أكثر من Detail)
    public List<PageDetailSetup> DetailSetups { get; set; } = new();

    // 3. قواعد أعمال إضافية
    public List<string>? BusinessRules { get; set; }
}

public class PageMasterSetup
{
    public string ListName { get; set; } = string.Empty;       // e.g. SalestView
    public string TableName { get; set; } = string.Empty;      // e.g. ContractViewWitPaid
    public string PrimaryKey { get; set; } = string.Empty;     // e.g. ContractID
    public string TailCondition { get; set; } = string.Empty;  // e.g. (IsResale=0...)

    // الأعمدة الخاصة بالـ Master
    public List<ColumnDefinition> Columns { get; set; } = new();
}

public class PageDetailSetup
{
    public string ListName { get; set; } = string.Empty;       // e.g. SalesDetailView
    public string TableName { get; set; } = string.Empty;      // e.g. salessystemcontract
    public string ForeignKey { get; set; } = string.Empty;     // e.g. ContractID

    // الأعمدة الخاصة بهذا الـ Detail فقط
    public List<ColumnDefinition> Columns { get; set; } = new();
}

public class ColumnDefinition
{
    public string SearchName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string InsertType { get; set; } = string.Empty;
    public string? DroModel { get; set; }
    public string? DroValue { get; set; }
    public string? DroText { get; set; }
    public string arColumnLabel { get; set; } = string.Empty;
    public string? enColumnLabel { get; set; }
}
