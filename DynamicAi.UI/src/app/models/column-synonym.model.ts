export interface ColumnSynonym {
    id: string;
    pageId: number;
    tableName: string;
    columnName: string;
    userTerm: string;
    language: string;
    isApproved: boolean;
    occurrenceCount: number;
    createdAt: string;
    approvedAt?: string;
    approvedBy?: string;
}
