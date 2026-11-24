import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ChatMessage } from '../../../models';
import {
    formatChatMarkdown,
    formatCostUsd,
    hasMessageTelemetry
} from '../../../utils/chat-response.mapper';

@Component({
    selector: 'app-chat-message-item',
    standalone: true,
    imports: [CommonModule],
    templateUrl: './chat-message-item.component.html',
    styleUrls: ['./chat-message-item.component.css']
})
export class ChatMessageItemComponent {
    @Input() msg?: ChatMessage;
    @Input() actionDisabled = false;
    @Input() isLoading = false;

    @Output() like = new EventEmitter<ChatMessage>();
    @Output() dislike = new EventEmitter<ChatMessage>();
    @Output() retry = new EventEmitter<{ question: string; retryCount?: number }>();
    @Output() copySql = new EventEmitter<string>();
    @Output() printTable = new EventEmitter<ChatMessage>();
    @Output() exportExcel = new EventEmitter<ChatMessage>();

    readonly hasTelemetry = hasMessageTelemetry;
    readonly formatCostUsd = formatCostUsd;
    readonly formatText = formatChatMarkdown;

    getKeys(obj: any): string[] {
        return obj ? Object.keys(obj) : [];
    }
}
