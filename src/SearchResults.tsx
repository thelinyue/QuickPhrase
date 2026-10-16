import { Fragment, type ReactNode } from 'react';
import { searchHitKey, type SearchHit } from './types';

/** 两处搜索共用先话术后文件的顺序；选中、编号和投递均使用该顺序，组内保持查询顺序。 */
export const orderSearchResults = (hits: SearchHit[]) => [...hits.filter(hit => hit.kind === 'phrase'), ...hits.filter(hit => hit.kind === 'file')];

export function SearchResults({ results, renderRow }: { results: SearchHit[]; renderRow: (hit: SearchHit, index: number) => ReactNode }) {
  const phrases = results.filter(hit => hit.kind === 'phrase'), files = results.filter(hit => hit.kind === 'file');
  return <>{([{ label: '话术', items: phrases, offset: 0 }, { label: '文件', items: files, offset: phrases.length }]).filter(group => group.items.length).map(group => <div className="search-result-group" role="group" aria-label={group.label} key={group.label}>
    <div className="search-group-title" aria-hidden="true">{group.label}</div>
    {group.items.map((hit, index) => <Fragment key={searchHitKey(hit)}>{renderRow(hit, group.offset + index)}</Fragment>)}
  </div>)}</>;
}
