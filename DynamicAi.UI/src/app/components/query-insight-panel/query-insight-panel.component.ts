import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { QueryInsight } from '../../models';

@Component({
    selector: 'app-query-insight-panel',
    standalone: true,
    imports: [CommonModule],
    templateUrl: './query-insight-panel.component.html',
    styleUrls: ['./query-insight-panel.component.css']
})
export class QueryInsightPanelComponent {
    @Input() insights?: QueryInsight;
    @Input() isExpanded: boolean = false;
}
