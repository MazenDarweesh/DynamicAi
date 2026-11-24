import { Injectable } from '@angular/core';
import { ChatMessage } from '../models';

@Injectable({
    providedIn: 'root'
})
export class ChatExportService {
    printTable(msg: ChatMessage): void {
        if (!msg.tableData || msg.tableData.length === 0) return;

        const printWindow = window.open('', '', 'height=800,width=1000');
        if (!printWindow) return;

        const timestamp = new Date().toLocaleString('en-EG', {
            dateStyle: 'medium',
            timeStyle: 'short'
        });
        const keys = Object.keys(msg.tableData[0] ?? {});
        const logoUrl = '/squareLogo.jpeg';

        const tableHtml = `
        <html>
        <head>
            <title>Dynamic AI - Print Data</title>
            <style>
                @page { margin: 0; size: auto; }
                body { font-family: 'Segoe UI', Arial, sans-serif; padding: 40px; color: #333; margin: 0; }
                .content-wrapper { padding: 40px; }
                .header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; border-bottom: 2px solid #eee; padding-bottom: 20px; }
                .logo-section { display: flex; align-items: center; gap: 15px; }
                .logo { height: 60px; width: auto; object-fit: contain; }
                .app-name { font-size: 1.5rem; font-weight: bold; color: #6366f1; }
                .meta { text-align: right; font-size: 0.9rem; color: #666; }
                h2 { margin: 0 0 10px 0; font-size: 1.4rem; color: #1e293b; }
                table { width: 100%; border-collapse: collapse; margin-bottom: 30px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }
                th, td { border: 1px solid #e2e8f0; padding: 12px; text-align: left; font-size: 0.95rem; }
                th { background-color: #f8fafc; font-weight: 600; color: #475569; -webkit-print-color-adjust: exact; }
                tr:nth-child(even) { background-color: #f1f5f9; -webkit-print-color-adjust: exact; }
                .question-box {
                    margin-bottom: 20px;
                    padding: 15px;
                    background-color: #f1f5f9;
                    border-left: 4px solid #6366f1;
                    font-size: 1.2rem;
                    font-weight: bold;
                    color: #1e293b;
                    -webkit-print-color-adjust: exact;
                }
                @media print { .no-print { display: none; } }
            </style>
        </head>
        <body>
            <div class="content-wrapper">
                <div class="header">
                    <div class="logo-section">
                        <img src="${logoUrl}" alt="Dynamic AI Logo" class="logo" />
                        <div class="app-name">Dynamic AI</div>
                    </div>
                    <div class="meta">
                        <div>Date: ${timestamp}</div>
                        <div>Rows: ${msg.tableData.length}</div>
                    </div>
                </div>
                <div class="question-box">${msg.originalQuestion || 'No question provided'}</div>
                <h2>Data Result</h2>
                <table>
                    <thead>
                        <tr>${keys.map((k) => `<th>${k}</th>`).join('')}</tr>
                    </thead>
                    <tbody>
                        ${msg.tableData
                            .map(
                                (row) => `
                            <tr>${keys.map((k) => `<td>${row[k]}</td>`).join('')}</tr>
                        `
                            )
                            .join('')}
                    </tbody>
                </table>
            </div>
            <script>
                window.onload = function() {
                    window.focus();
                    setTimeout(function() { window.print(); }, 500);
                };
                window.onafterprint = function() { window.close(); };
            </script>
        </body>
        </html>`;

        printWindow.document.write(tableHtml);
        printWindow.document.close();
    }

    async exportExcel(msg: ChatMessage): Promise<void> {
        if (!msg.tableData || msg.tableData.length === 0) return;

        try {
            const xlsx = await import('xlsx');
            const worksheet = xlsx.utils.json_to_sheet(msg.tableData);
            const workbook = xlsx.utils.book_new();
            xlsx.utils.book_append_sheet(workbook, worksheet, 'Data');

            const timestamp = new Date().toISOString().slice(0, 19).replace(/:/g, '-');
            const sanitizedQ = (msg.originalQuestion || 'data')
                .replace(/[^a-z0-9]/gi, '_')
                .substring(0, 20);
            const fileName = `DynamicAI_${sanitizedQ}_${timestamp}.xlsx`;

            xlsx.writeFile(workbook, fileName);
        } catch (error) {
            console.error('❌ Export failed:', error);
            alert('Failed to export Excel. Please try again.');
        }
    }
}
