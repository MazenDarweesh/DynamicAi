import { Component, OnInit, inject, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TrainingService } from '../../services/training.service';
import { TrainingStats, ManualTrainingRequest } from '../../models';

@Component({
    selector: 'app-training-data',
    standalone: true,
    imports: [CommonModule, FormsModule],
    templateUrl: './training-data.component.html',
    styleUrls: ['./training-data.component.css']
})
export class TrainingDataComponent implements OnInit {
    private readonly trainingService = inject(TrainingService);
    private readonly cdr = inject(ChangeDetectorRef);

    stats: TrainingStats | null = null;
    loading = true;
    exporting = false;
    error: string | null = null;
    exportMessage: string | null = null;

    // Pending examples properties
    pendingExamples: any[] = [];
    loadingPending = false;
    processingId: string | null = null;

    // Manual form properties
    manualForm: ManualTrainingRequest = {
        naturalQuery: '',
        sql: '',
        notes: ''
    };
    submitting = false;
    formMessage: string | null = null;

    ngOnInit() {
        this.loadStats();
        this.loadPendingExamples();
    }

    loadStats() {
        this.loading = true;
        this.error = null;
        this.trainingService.getStats().subscribe({
            next: (data) => {
                this.stats = data;
                this.loading = false;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.error = err?.message || 'Failed to load statistics';
                this.loading = false;
                this.cdr.detectChanges();
            }
        });
    }

    exportTrainingData(verifiedOnly: boolean) {
        this.exporting = true;
        this.exportMessage = null;
        this.error = null;

        this.trainingService.exportData(verifiedOnly).subscribe({
            next: (blob) => {
                // Create download link
                const url = window.URL.createObjectURL(blob);
                const link = document.createElement('a');
                link.href = url;
                link.download = `training_data_${this.getCurrentDate()}.jsonl`;
                link.click();
                window.URL.revokeObjectURL(url);

                this.exportMessage = `✅ Exported successfully!`;
                this.exporting = false;
                this.cdr.detectChanges();

                // Clear message after 3 seconds
                setTimeout(() => {
                    this.exportMessage = null;
                    this.cdr.detectChanges();
                }, 3000);
            },
            error: (err) => {
                this.error = err?.message || 'Export failed';
                this.exporting = false;
                this.cdr.detectChanges();
            }
        });
    }

    submitManualExample() {
        if (!this.manualForm.naturalQuery.trim() || !this.manualForm.sql.trim()) {
            this.formMessage = '⚠️ يجب إدخال السؤال والـ SQL';
            return;
        }

        this.submitting = true;
        this.formMessage = null;

        this.trainingService.addManualExample(this.manualForm).subscribe({
            next: (response) => {
                if (response.success) {
                    this.formMessage = response.message;
                    // Reset form
                    this.manualForm = { naturalQuery: '', sql: '', notes: '' };
                    // Reload stats
                    this.loadStats();
                } else {
                    this.formMessage = response.message;
                }
                this.submitting = false;
                this.cdr.detectChanges();

                // Clear message after 5 seconds
                setTimeout(() => {
                    this.formMessage = null;
                    this.cdr.detectChanges();
                }, 5000);
            },
            error: (err) => {
                this.formMessage = err?.error?.message || err?.message || 'فشل في إضافة المثال';
                this.submitting = false;
                this.cdr.detectChanges();
            }
        });
    }

    private getCurrentDate(): string {
        const now = new Date();
        const year = now.getFullYear();
        const month = String(now.getMonth() + 1).padStart(2, '0');
        const day = String(now.getDate()).padStart(2, '0');
        return `${year}${month}${day}`;
    }

    getProgressColor(): string {
        if (!this.stats) return '#6b7280';
        if (this.stats.progress >= 80) return '#10b981';
        if (this.stats.progress >= 50) return '#f59e0b';
        return '#ef4444';
    }

    loadPendingExamples() {
        this.loadingPending = true;
        this.trainingService.getPendingExamples().subscribe({
            next: (data) => {
                this.pendingExamples = data;
                this.loadingPending = false;
                this.cdr.detectChanges();
            },
            error: (err) => {
                console.error('Error loading pending examples:', err);
                this.loadingPending = false;
                this.cdr.detectChanges();
            }
        });
    }

    verifyExample(id: string) {
        this.processingId = id;
        this.trainingService.verifyExample(id).subscribe({
            next: () => {
                this.processingId = null;
                this.loadPendingExamples();
                this.loadStats();
                this.cdr.detectChanges();
            },
            error: (err) => {
                console.error('Error verifying example:', err);
                this.processingId = null;
                this.cdr.detectChanges();
            }
        });
    }

    rejectExample(id: string) {
        if (!confirm('هل أنت متأكد من حذف هذا المثال؟')) {
            return;
        }
        this.processingId = id;
        this.trainingService.rejectExample(id).subscribe({
            next: () => {
                this.processingId = null;
                this.loadPendingExamples();
                this.loadStats();
                this.cdr.detectChanges();
            },
            error: (err) => {
                console.error('Error rejecting example:', err);
                this.processingId = null;
                this.cdr.detectChanges();
            }
        });
    }
}
