import { Component, OnInit, inject, ChangeDetectorRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { IntentReviewService } from '../../services/intent-review.service';
import { IntentReviewItemDto, ClearIntentRequest, ClearCacheRequest } from '../../models';

@Component({
    selector: 'app-intent-review',
    standalone: true,
    imports: [CommonModule, FormsModule],
    templateUrl: './intent-review.component.html',
    styleUrls: ['./intent-review.component.css']
})
export class IntentReviewComponent implements OnInit {
    private readonly intentReview = inject(IntentReviewService);
    private readonly cdr = inject(ChangeDetectorRef);

    pending: IntentReviewItemDto[] = [];
    pendingCount = 0;
    loading = true;
    clearQuestion = '';
    clearLoading = false;
    clearMessage: string | null = null;

    // Cache clearing
    clearCacheQuestion = '';
    clearCacheLoading = false;
    clearCacheMessage: string | null = null;

    error: string | null = null;

    ngOnInit() {
        this.loadData();
    }

    loadData() {
        this.loading = true;
        this.error = null;
        this.intentReview.getPending().subscribe({
            next: (list) => {
                this.pending = list;
                this.pendingCount = list.length;
                this.loading = false;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.error = err?.message || 'Failed to load pending examples';
                this.loading = false;
                this.cdr.detectChanges();
            }
        });
    }

    approve(item: IntentReviewItemDto) {
        this.intentReview.approve(item.id).subscribe({
            next: () => {
                this.pending = this.pending.filter((p) => p.id !== item.id);
                this.pendingCount = this.pending.length;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.error = err?.message || 'Approve failed';
                this.cdr.detectChanges();
            }
        });
    }

    reject(item: IntentReviewItemDto) {
        this.intentReview.reject(item.id).subscribe({
            next: () => {
                this.pending = this.pending.filter((p) => p.id !== item.id);
                this.pendingCount = this.pending.length;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.error = err?.message || 'Reject failed';
                this.cdr.detectChanges();
            }
        });
    }

    clearByQuestion() {
        const q = (this.clearQuestion || '').trim();
        if (!q) return;
        this.clearLoading = true;
        this.clearMessage = null;
        const req: ClearIntentRequest = {
            question: q,
            tenantId: 'default', // TODO: Get from AuthService
            companyId: 1        // TODO: Get from AuthService
        };
        this.intentReview.clearByQuestion(req).subscribe({
            next: (res) => {
                this.clearMessage = res.message || 'Done';
                this.clearQuestion = '';
                this.clearLoading = false;
                this.loadData();
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.clearMessage = err?.error?.error || err?.message || 'Clear failed';
                this.clearLoading = false;
                this.cdr.detectChanges();
            }
        });
    }

    clearCacheByQuestion() {
        const q = (this.clearCacheQuestion || '').trim();
        if (!q) return;
        this.clearCacheLoading = true;
        this.clearCacheMessage = null;
        const req: ClearCacheRequest = { question: q };
        this.intentReview.clearCacheByQuestion(req).subscribe({
            next: (res) => {
                this.clearCacheMessage = res.message || 'Done';
                this.clearCacheQuestion = '';
                this.clearCacheLoading = false;
                this.cdr.detectChanges();
            },
            error: (err) => {
                this.clearCacheMessage = err?.error?.error || err?.message || 'Clear failed';
                this.clearCacheLoading = false;
                this.cdr.detectChanges();
            }
        });
    }
}
