using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Urlog.Core.Metadata;

/// <summary>
/// Replaces the SQLite provider's relational annotation provider, whose contribution to the model is
/// <c>Sqlite:Autoincrement</c> on every integer key (plus SpatiaLite's SRID, unused here): that annotation becomes the
/// <c>AUTOINCREMENT</c> keyword and a <c>sqlite_sequence</c> table in every migration, both SQLite-only and both
/// forbidden by the portability rule. Without it an <c>INTEGER PRIMARY KEY</c> column is still the rowid alias
/// and SQLite still assigns <c>max(id) + 1</c>; the one behavioural difference is that an id can be reused after the
/// row holding the highest id is deleted, which the design record notes for purge. No annotation at all is exactly the
/// portable set.
/// </summary>
internal sealed class PlainRelationalAnnotationProvider : IRelationalAnnotationProvider
{
    public IEnumerable<IAnnotation> For(IRelationalModel model, bool designTime) => [];

    public IEnumerable<IAnnotation> For(ITable table, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IColumn column, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IView view, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IViewColumn column, bool designTime) => [];

    public IEnumerable<IAnnotation> For(ISqlQuery sqlQuery, bool designTime) => [];

    public IEnumerable<IAnnotation> For(ISqlQueryColumn column, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IStoreFunction function, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IFunctionColumn column, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IStoreFunctionParameter parameter, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IStoreStoredProcedure storedProcedure, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IStoreStoredProcedureParameter parameter, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IStoreStoredProcedureResultColumn column, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IUniqueConstraint constraint, bool designTime) => [];

    public IEnumerable<IAnnotation> For(ITableIndex index, bool designTime) => [];

    public IEnumerable<IAnnotation> For(IForeignKeyConstraint foreignKey, bool designTime) => [];

    public IEnumerable<IAnnotation> For(ISequence sequence, bool designTime) => [];

    public IEnumerable<IAnnotation> For(ICheckConstraint checkConstraint, bool designTime) => [];

    public IEnumerable<IAnnotation> For(ITrigger trigger, bool designTime) => [];
}
