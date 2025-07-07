using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ZViewer.Models;
using Expression = System.Linq.Expressions.Expression;

namespace ZViewer.Services
{
    public interface IFilterService
    {
        bool MatchesEventIdFilter(int eventId, string filterExpression);
        System.Linq.Expressions.Expression<Func<EventLogEntry, bool>> BuildFilterExpression(FilterCriteria criteria);
        IEnumerable<int> ParseEventIdFilter(string filterExpression);
    }

    public class FilterService : IFilterService
    {
        private readonly ILoggingService _loggingService;

        public FilterService(ILoggingService loggingService)
        {
            _loggingService = loggingService;
        }

        public bool MatchesEventIdFilter(int eventId, string filterExpression)
        {
            if (string.IsNullOrWhiteSpace(filterExpression))
                return true;

            try
            {
                var includes = new List<int>();
                var excludes = new List<int>();
                var ranges = new List<(int start, int end)>();
                var excludeRanges = new List<(int start, int end)>();

                // Parse the filter expression
                var parts = filterExpression.Split(',', StringSplitOptions.RemoveEmptyEntries);

                foreach (var part in parts)
                {
                    var trimmed = part.Trim();

                    if (trimmed.StartsWith("-"))
                    {
                        // Exclusion
                        var value = trimmed.Substring(1);
                        if (value.Contains("-"))
                        {
                            // Exclude range
                            var rangeParts = value.Split('-');
                            if (rangeParts.Length == 2 &&
                                int.TryParse(rangeParts[0], out var start) &&
                                int.TryParse(rangeParts[1], out var end))
                            {
                                excludeRanges.Add((Math.Min(start, end), Math.Max(start, end)));
                            }
                        }
                        else if (int.TryParse(value, out var id))
                        {
                            excludes.Add(id);
                        }
                    }
                    else if (trimmed.Contains("-"))
                    {
                        // Include range
                        var rangeParts = trimmed.Split('-');
                        if (rangeParts.Length == 2 &&
                            int.TryParse(rangeParts[0], out var start) &&
                            int.TryParse(rangeParts[1], out var end))
                        {
                            ranges.Add((Math.Min(start, end), Math.Max(start, end)));
                        }
                    }
                    else if (int.TryParse(trimmed, out var id))
                    {
                        includes.Add(id);
                    }
                }

                // Check exclusions first
                if (excludes.Contains(eventId))
                    return false;

                if (excludeRanges.Any(r => eventId >= r.start && eventId <= r.end))
                    return false;

                // If no includes specified, include all (except exclusions)
                if (!includes.Any() && !ranges.Any())
                    return true;

                // Check inclusions
                if (includes.Contains(eventId))
                    return true;

                if (ranges.Any(r => eventId >= r.start && eventId <= r.end))
                    return true;

                return false;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning("Error parsing event ID filter: {Error}", ex.Message);
                return true; // Default to include on error
            }
        }

        public System.Linq.Expressions.Expression<Func<EventLogEntry, bool>> BuildFilterExpression(FilterCriteria criteria)
        {
            var parameter = Expression.Parameter(typeof(EventLogEntry), "e");
            Expression? body = null;

            // Level filtering
            if (HasLevelFilter(criteria))
            {
                var levelProperty = Expression.Property(parameter, "Level");
                var levelChecks = new List<Expression>();

                if (criteria.IncludeCritical)
                    levelChecks.Add(Expression.Equal(levelProperty, Expression.Constant("Critical")));
                if (criteria.IncludeError)
                    levelChecks.Add(Expression.Equal(levelProperty, Expression.Constant("Error")));
                if (criteria.IncludeWarning)
                    levelChecks.Add(Expression.Equal(levelProperty, Expression.Constant("Warning")));
                if (criteria.IncludeInformation)
                    levelChecks.Add(Expression.Equal(levelProperty, Expression.Constant("Information")));
                if (criteria.IncludeVerbose)
                    levelChecks.Add(Expression.Equal(levelProperty, Expression.Constant("Verbose")));

                if (levelChecks.Any())
                {
                    body = CombineOr(levelChecks);
                }
            }

            // Event ID filtering
            if (!string.IsNullOrWhiteSpace(criteria.EventIds) &&
                !criteria.EventIds.Equals("<All Event IDs>", StringComparison.OrdinalIgnoreCase))
            {
                var eventIdProperty = Expression.Property(parameter, "EventId");
                var methodInfo = typeof(IFilterService).GetMethod(nameof(MatchesEventIdFilter));
                var thisExpression = Expression.Constant(this);
                var filterExpression = Expression.Constant(criteria.EventIds);

                var eventIdCheck = Expression.Call(
                    thisExpression,
                    methodInfo,
                    eventIdProperty,
                    filterExpression);

                body = CombineAnd(body, eventIdCheck);
            }

            // Source filtering
            if (!string.IsNullOrWhiteSpace(criteria.Source))
            {
                var sourceProperty = Expression.Property(parameter, "Source");
                var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string), typeof(StringComparison) });
                var sourceCheck = Expression.Call(
                    sourceProperty,
                    containsMethod,
                    Expression.Constant(criteria.Source),
                    Expression.Constant(StringComparison.OrdinalIgnoreCase));

                body = CombineAnd(body, sourceCheck);
            }

            // Keywords filtering
            if (!string.IsNullOrWhiteSpace(criteria.Keywords))
            {
                var descProperty = Expression.Property(parameter, "Description");
                var containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string), typeof(StringComparison) });
                var keywordCheck = Expression.Call(
                    descProperty,
                    containsMethod,
                    Expression.Constant(criteria.Keywords),
                    Expression.Constant(StringComparison.OrdinalIgnoreCase));

                body = CombineAnd(body, keywordCheck);
            }

            // User filtering
            if (!string.IsNullOrWhiteSpace(criteria.User) &&
                !criteria.User.Equals("<All Users>", StringComparison.OrdinalIgnoreCase))
            {
                var userProperty = Expression.Property(parameter, "User");
                var userCheck = Expression.Equal(userProperty, Expression.Constant(criteria.User));
                body = CombineAnd(body, userCheck);
            }

            // Computer filtering
            if (!string.IsNullOrWhiteSpace(criteria.Computer) &&
                !criteria.Computer.Equals("<All Computers>", StringComparison.OrdinalIgnoreCase))
            {
                // For now, we'll skip computer filtering as it's not in the EventLogEntry model
                // This could be added if the model is extended
            }

            return System.Linq.Expressions.Expression.Lambda<Func<EventLogEntry, bool>>(
                body ?? Expression.Constant(true),
                parameter);
        }

        public IEnumerable<int> ParseEventIdFilter(string filterExpression)
        {
            var result = new HashSet<int>();

            if (string.IsNullOrWhiteSpace(filterExpression))
                return result;

            try
            {
                var parts = filterExpression.Split(',', StringSplitOptions.RemoveEmptyEntries);

                foreach (var part in parts)
                {
                    var trimmed = part.Trim();

                    // Skip exclusions
                    if (trimmed.StartsWith("-"))
                        continue;

                    if (trimmed.Contains("-"))
                    {
                        // Range
                        var rangeParts = trimmed.Split('-');
                        if (rangeParts.Length == 2 &&
                            int.TryParse(rangeParts[0], out var start) &&
                            int.TryParse(rangeParts[1], out var end))
                        {
                            for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++)
                            {
                                result.Add(i);
                            }
                        }
                    }
                    else if (int.TryParse(trimmed, out var id))
                    {
                        result.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning("Error parsing event IDs: {Error}", ex.Message);
            }

            return result;
        }

        private bool HasLevelFilter(FilterCriteria criteria)
        {
            return criteria.IncludeCritical ||
                   criteria.IncludeError ||
                   criteria.IncludeWarning ||
                   criteria.IncludeInformation ||
                   criteria.IncludeVerbose;
        }

        private Expression? CombineAnd(Expression? left, Expression right)
        {
            return left == null ? right : Expression.AndAlso(left, right);
        }

        private Expression? CombineOr(IEnumerable<Expression> expressions)
        {
            return expressions.Aggregate<Expression, Expression?>(
                null,
                (current, expr) => current == null ? expr : Expression.OrElse(current, expr));
        }
    }
}