import { ShippedPageContext } from '../models';

/**
 * Shipped Page 244 (فواتير المبيعات) context preset for POC Directed Mode.
 * Derived from tests/DynamicAI.Tests/Fixtures/directed_request_l1.json.
 */
export const PAGE_244_PRESET_CONTEXT: ShippedPageContext = {
    pageId: 244,
    pageName: 'فواتير المبيعات',
    masterSetup: {
        listName: 'SalestView',
        tableName: 'ContractViewWithPaid',
        primaryKey: 'ContractID',
        tailCondition:
            '(IsResale=0 or IsResale is null) and ( ContractType!=45 or ContractType!=51 or ContractType!=52 ) and  ((EInvoiceIsCredit =0 or EInvoiceIsCredit is null ) and (EInvoiceIsDepit =0 or EInvoiceIsDepit is null) )',
        columns: [
            {
                searchName: 'ContractID',
                columnName: 'ContractID',
                insertType: 'number',
                arColumnLabel: 'رقم العقد',
                enColumnLabel: 'Contract ID',
                isVisible: true
            },
            {
                searchName: 'AutoNumber',
                columnName: 'AutoNumber',
                insertType: 'number',
                arColumnLabel: 'رقم الفاتورة',
                enColumnLabel: 'Invoice number',
                isVisible: true
            },
            {
                searchName: 'CustomerID',
                columnName: 'CustomerID',
                insertType: 'dropdown',
                droModel: 'CustomerAccountView',
                droValue: 'CustomerAccountID',
                droText: 'CustomerAccountName',
                arColumnLabel: 'اسم العميل',
                enColumnLabel: 'Client name',
                isVisible: true
            },
            {
                searchName: 'ContractDate',
                columnName: 'ContractDate',
                insertType: 'date',
                arColumnLabel: 'التاريخ',
                enColumnLabel: 'Date',
                isVisible: true
            },
            {
                searchName: 'ContractType',
                columnName: 'ContractType',
                insertType: 'dropdown',
                droModel: 'ConType',
                droValue: 'ContractTypeID',
                droText: 'TypeName',
                arColumnLabel: 'طريقة البيع',
                enColumnLabel: 'Sale method',
                isVisible: true
            },
            {
                searchName: 'SalesMan',
                columnName: 'SalesMan',
                insertType: 'dropdown',
                droModel: 'SalesmenView',
                droValue: 'EmployeeID',
                droText: 'EmployeeNameA',
                arColumnLabel: 'رجل المبيعات',
                enColumnLabel: 'Sales Man',
                isVisible: true
            },
            {
                searchName: 'TotalOrder',
                columnName: 'TotalOrder',
                insertType: 'number',
                arColumnLabel: 'قيمة الفاتورة',
                enColumnLabel: 'Invoice value',
                isVisible: true
            },
            {
                searchName: 'Discount',
                columnName: 'Discount',
                insertType: 'number',
                arColumnLabel: 'الخصم',
                enColumnLabel: 'Discount',
                isVisible: true
            },
            {
                searchName: 'TaxCurrancy',
                columnName: 'TaxCurrancy',
                insertType: 'number',
                arColumnLabel: 'ضريبة القيمة المضافة',
                enColumnLabel: 'VAT',
                isVisible: true
            },
            {
                searchName: 'totalPaid',
                columnName: 'totalPaid',
                insertType: 'number',
                arColumnLabel: 'اجمالي المحصل',
                enColumnLabel: 'Total Paid in cash',
                isVisible: true
            },
            {
                searchName: 'remindpaid',
                columnName: 'remindpaid',
                insertType: 'number',
                arColumnLabel: 'المتبقي',
                enColumnLabel: 'Remain',
                isVisible: true
            },
            {
                searchName: 'finaltotal',
                columnName: 'finaltotal',
                insertType: 'number',
                arColumnLabel: 'الاجمالي',
                enColumnLabel: 'Total',
                isVisible: true
            },
            {
                searchName: 'IsPaid',
                columnName: 'IsPaid',
                insertType: 'checkbox',
                arColumnLabel: 'تم التحصيل',
                enColumnLabel: 'Collected',
                isVisible: true
            },
            {
                searchName: 'CreateBy',
                columnName: 'CreateBy',
                insertType: 'dropdown',
                droModel: 'EmployeeView',
                droValue: 'EmployeeID',
                droText: 'EmployeeNameA',
                arColumnLabel: 'ادخال بواسطة',
                enColumnLabel: 'Inserted by',
                isVisible: true
            },
            {
                searchName: 'CreateAt',
                columnName: 'CreateAt',
                insertType: 'date',
                arColumnLabel: 'تاريخ الادخال',
                enColumnLabel: 'Entry date',
                isVisible: true
            },
            {
                searchName: 'ModifyBy',
                columnName: 'ModifyBy',
                insertType: 'dropdown',
                droModel: 'EmployeeView',
                droValue: 'EmployeeID',
                droText: 'EmployeeNameA',
                arColumnLabel: 'اخر تعديل بواسطة',
                enColumnLabel: 'Last update',
                isVisible: true
            },
            {
                searchName: 'ModifyAt',
                columnName: 'ModifyAt',
                insertType: 'date',
                arColumnLabel: 'اخر تعديل في',
                enColumnLabel: 'Last Edit In',
                isVisible: true
            }
        ]
    },
    detailSetups: [
        {
            listName: 'SalesDetailView',
            tableName: 'salessystemcontract',
            foreignKey: 'ContractID',
            tailCondition: null,
            columns: [
                {
                    searchName: 'ProductID',
                    columnName: 'ProductID',
                    insertType: 'dropdown',
                    droModel: 'ProductPurchase',
                    droValue: 'ProID',
                    droText: 'ProName',
                    arColumnLabel: 'اسم المنتج',
                    enColumnLabel: 'Product name',
                    isVisible: true
                },
                {
                    searchName: 'Qty',
                    columnName: 'Qty',
                    insertType: 'number',
                    arColumnLabel: 'الكمية',
                    enColumnLabel: 'Quantity',
                    isVisible: true
                },
                {
                    searchName: 'PriceCurrancy',
                    columnName: 'PriceCurrancy',
                    insertType: 'number',
                    arColumnLabel: 'السعر',
                    enColumnLabel: 'Price',
                    isVisible: true
                },
                {
                    searchName: 'UnitID',
                    columnName: 'UnitID',
                    insertType: 'dropdown',
                    droModel: 'Units',
                    droValue: 'UID',
                    droText: 'UName',
                    arColumnLabel: 'الوحدة',
                    enColumnLabel: 'Unit',
                    isVisible: true
                },
                {
                    searchName: 'StoreID',
                    columnName: 'StoreID',
                    insertType: 'dropdown',
                    droModel: 'StoreView',
                    droValue: 'SID',
                    droText: 'SName',
                    arColumnLabel: 'المخزن',
                    enColumnLabel: 'Inventory',
                    isVisible: true
                },
                {
                    searchName: 'DiscountDetailValue',
                    columnName: 'DiscountDetailValue',
                    insertType: 'number',
                    arColumnLabel: 'قيمة الخصم',
                    enColumnLabel: 'Discount value',
                    isVisible: true
                },
                {
                    searchName: 'TaxDetailValue',
                    columnName: 'TaxDetailValue',
                    insertType: 'number',
                    arColumnLabel: 'قيمة الضريبة',
                    enColumnLabel: 'Tax Value',
                    isVisible: true
                },
                {
                    searchName: 'Total',
                    columnName: 'Total',
                    insertType: 'number',
                    arColumnLabel: 'الاجمالي',
                    enColumnLabel: 'Total',
                    isVisible: true
                },
                {
                    searchName: 'CurrancyID',
                    columnName: 'CurrancyID',
                    insertType: 'dropdown',
                    droModel: 'Currancy',
                    droValue: 'CID',
                    droText: 'CName',
                    arColumnLabel: 'عملة البيع',
                    enColumnLabel: 'Selling currency',
                    isVisible: true
                }
            ]
        }
    ]
};
