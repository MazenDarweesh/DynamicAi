export interface ColumnDefinition {
    searchName: string;
    columnName: string;
    insertType: string;
    droModel?: string | null;
    droValue?: string | null;
    droText?: string | null;
    arColumnLabel: string;
    enColumnLabel?: string | null;
    isVisible?: boolean;
}

export interface PageMasterSetup {
    listName: string;
    tableName: string;
    primaryKey: string;
    tailCondition?: string | null;
    columns: ColumnDefinition[];
}

export interface PageDetailSetup {
    listName: string;
    tableName: string;
    foreignKey: string;
    tailCondition?: string | null;
    columns: ColumnDefinition[];
}

export interface ShippedPageContext {
    pageId: number;
    pageName: string;
    masterSetup: PageMasterSetup;
    detailSetups: PageDetailSetup[];
    businessRules?: string[] | null;
}

export interface DirectedPageAskRequest {
    question: string;
    tenantId: string;
    tenantKey?: string | null;
    comID?: number | null;
    sessionId?: string | null;
    pageId: number;
    connectionString?: string | null;
    previousQuestion?: string | null;
    previousSql?: string | null;
    explicitContext: ShippedPageContext;
}
