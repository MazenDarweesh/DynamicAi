import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ChatMessage } from '../../models';
import { ChatSignalService } from '../../services/chat-signal.service';

interface InsightCard {
    date: Date;
    question: string;
    context: string[];
    reasoning: string;
    isReadOnly: boolean;
}

@Component({
    selector: 'app-query-insights-page',
    standalone: true,
    imports: [CommonModule],
    templateUrl: './query-insights-page.component.html',
    styleUrls: ['./query-insights-page.component.css']
})
export class QueryInsightsPageComponent implements OnInit {
    private readonly chatSignal = inject(ChatSignalService);

    insights: InsightCard[] = [];
    loading = false;

    ngOnInit(): void {
        this.loadInsights();
    }

    loadInsights(): void {
        this.loading = true;

        // Get messages from ChatSignalService
        const state = this.chatSignal.getState();
        const messages: ChatMessage[] = state.messages || [];

        this.insights = messages
            .filter((msg: ChatMessage) => msg.queryInsights)
            .map((msg: ChatMessage) => ({
                date: new Date(),
                question: msg.text || 'N/A',
                context: msg.queryInsights?.retrievedContext || [],  // Fix: use retrievedContext
                reasoning: msg.queryInsights?.reasoning || 'No reasoning provided',
                isReadOnly: msg.queryInsights?.isReadOnly ?? true    // Fix: use isReadOnly
            }));

        this.loading = false;
    }

    refresh(): void {
        this.loadInsights();
    }
}
