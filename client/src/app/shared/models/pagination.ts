export type Pagination<T> = {
    value: any;
    pageIndex: number;
    pageSize: number;
    count: number;
    data: T[]
}