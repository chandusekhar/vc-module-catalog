﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Web.Http;
using System.Web.Http.Description;
using VirtoCommerce.CatalogModule.Data.Search.BrowseFilters;
using VirtoCommerce.CatalogModule.Web.Security;
using VirtoCommerce.Domain.Catalog.Model;
using VirtoCommerce.Domain.Catalog.Services;
using VirtoCommerce.Domain.Store.Model;
using VirtoCommerce.Domain.Store.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Security;

namespace VirtoCommerce.CatalogModule.Web.Controllers.Api
{
    [RoutePrefix("api/catalog/aggregationproperties")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class CatalogBrowseFiltersController : CatalogBaseController
    {
        private const string _attributeType = "Attribute";
        private const string _rangeType = "Range";
        private const string _priceRangeType = "PriceRange";

        private readonly IStoreService _storeService;
        private readonly IPropertyService _propertyService;
        private readonly IBrowseFilterService _browseFilterService;

        public CatalogBrowseFiltersController(ISecurityService securityService, IPermissionScopeService permissionScopeService, IStoreService storeService, IPropertyService propertyService, IBrowseFilterService browseFilterService)
            : base(securityService, permissionScopeService)
        {
            _storeService = storeService;
            _propertyService = propertyService;
            _browseFilterService = browseFilterService;
        }

        /// <summary>
        /// Get browse filter properties for store
        /// </summary>
        /// <remarks>
        /// Returns all store catalog properties: selected properties are ordered manually, unselected properties are ordered by name.
        /// </remarks>
        /// <param name="storeId">Store ID</param>
        [HttpGet]
        [Route("{storeId}/properties")]
        [ResponseType(typeof(BrowseFilterProperty[]))]
        public IHttpActionResult GetAggregationProperties(string storeId)
        {
            var store = _storeService.GetById(storeId);
            if (store == null)
            {
                return StatusCode(HttpStatusCode.NoContent);
            }

            CheckCurrentUserHasPermissionForObjects(CatalogPredefinedPermissions.ReadBrowseFilters, store);

            var allProperties = GetAllProperties(store);
            var selectedProperties = GetSelectedProperties(store.Id);

            // Remove duplicates and keep selected properties order
            var result = selectedProperties.Concat(allProperties)
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            return Ok(result);
        }

        /// <summary>
        /// Set browse filter properties for store
        /// </summary>
        /// <param name="storeId">Store ID</param>
        /// <param name="browseFilterProperties"></param>
        [HttpPut]
        [Route("{storeId}/properties")]
        [ResponseType(typeof(void))]
        public IHttpActionResult SetAggregationProperties(string storeId, BrowseFilterProperty[] browseFilterProperties)
        {
            var store = _storeService.GetById(storeId);
            if (store == null)
            {
                return StatusCode(HttpStatusCode.NoContent);
            }

            CheckCurrentUserHasPermissionForObjects(CatalogPredefinedPermissions.UpdateBrowseFilters, store);

            // Filter names must be unique
            // Keep the selected properties order.
            var filters = browseFilterProperties
                .Where(p => p.IsSelected)
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select((g, i) => ConvertToFilter(g.First(), i))
                .Where(f => f != null)
                .ToArray();

            _browseFilterService.SetAllFilters(store, filters);
            _storeService.Update(new[] { store });

            return StatusCode(HttpStatusCode.NoContent);
        }

        [HttpGet]
        [Route("{storeId}/properties/{propertyName}/values")]
        [ResponseType(typeof(string[]))]
        public IHttpActionResult GetPropertyValues(string storeId, string propertyName)
        {
            var store = _storeService.GetById(storeId);
            if (store == null)
            {
                return StatusCode(HttpStatusCode.NoContent);
            }

            CheckCurrentUserHasPermissionForObjects(CatalogPredefinedPermissions.ReadBrowseFilters, store);

            var result = GetAllCatalogProperties(store.Catalog)
                .Where(p => p.Name.EqualsInvariant(propertyName) && p.Dictionary && p.DictionaryValues != null)
                .SelectMany(p => p.DictionaryValues.Select(v => v.Alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Ok(result);
        }


        private IList<BrowseFilterProperty> GetAllProperties(Store store)
        {
            var result = GetAllCatalogProperties(store.Catalog)
                .Select(p => new BrowseFilterProperty { Type = _attributeType, Name = p.Name })
                .ToList();

            result.AddRange(store.Currencies.Select(c => new BrowseFilterProperty { Type = _priceRangeType, Name = $"Price {c}", Currency = c }));

            return result;
        }

        private IEnumerable<Property> GetAllCatalogProperties(string catalogId)
        {
            return _propertyService.GetAllCatalogProperties(catalogId)
                .OrderBy(p => p?.Name, StringComparer.OrdinalIgnoreCase)
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First());
        }

        private IList<BrowseFilterProperty> GetSelectedProperties(string storeId)
        {
            var result = new List<BrowseFilterProperty>();

            var allFilters = _browseFilterService.GetAllFilters(storeId);

            BrowseFilterProperty property = null;

            foreach (var filter in allFilters)
            {
                var attributeFilter = filter as AttributeFilter;
                var rangeFilter = filter as RangeFilter;
                var priceRangeFilter = filter as PriceRangeFilter;

                if (attributeFilter != null)
                {
                    property = new BrowseFilterProperty
                    {
                        IsSelected = true,
                        Type = _attributeType,
                        Name = attributeFilter.Key,
                        Values = attributeFilter.Values?.Select(v => v.Id).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
                        Size = attributeFilter.FacetSize,
                    };
                }
                else if (rangeFilter != null)
                {
                    property = new BrowseFilterProperty
                    {
                        IsSelected = true,
                        Type = _rangeType,
                        Name = rangeFilter.Key,
                        Values = GetRangeBounds(rangeFilter.Values),
                    };
                }
                else if (priceRangeFilter != null)
                {
                    property = new BrowseFilterProperty
                    {
                        IsSelected = true,
                        Type = _priceRangeType,
                        Name = $"Price {priceRangeFilter.Currency}",
                        Values = GetRangeBounds(priceRangeFilter.Values),
                        Currency = priceRangeFilter.Currency,
                    };
                }

                if (property != null)
                {
                    result.Add(property);
                }
            }

            return result;
        }

        private static IList<string> GetRangeBounds(IEnumerable<RangeFilterValue> values)
        {
            return SortStringsAsNumbers(values.SelectMany(v => new[] { v.Lower, v.Upper })).ToArray();
        }

        private static IBrowseFilter ConvertToFilter(BrowseFilterProperty property, int order)
        {
            IBrowseFilter result = null;

            switch (property.Type)
            {
                case _attributeType:
                    result = new AttributeFilter
                    {
                        Order = order,
                        Key = property.Name,
                        FacetSize = property.Size,
                        Values = property.Values?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).Select(v => new AttributeFilterValue { Id = v }).ToArray(),
                    };
                    break;
                case _rangeType:
                    result = new RangeFilter
                    {
                        Order = order,
                        Key = property.Name,
                        Values = GetRangeFilterValues(property.Values),
                    };
                    break;
                case _priceRangeType:
                    result = new PriceRangeFilter
                    {
                        Order = order,
                        Currency = property.Currency,
                        Values = GetRangeFilterValues(property.Values),
                    };
                    break;
            }

            return result;
        }

        private static RangeFilterValue[] GetRangeFilterValues(IList<string> bounds)
        {
            var result = new List<RangeFilterValue>();

            if (bounds != null)
            {
                var sortedBounds = SortStringsAsNumbers(bounds).ToList();
                sortedBounds.Add(null);

                string previousBound = null;

                foreach (var bound in sortedBounds)
                {
                    var value = new RangeFilterValue
                    {
                        Id = previousBound == null ? $"under-{bound}" : bound == null ? $"over-{previousBound}" : $"{previousBound}-{bound}",
                        Lower = previousBound,
                        Upper = bound,
                        IncludeLower = true,
                        IncludeUpper = false,
                    };

                    result.Add(value);
                    previousBound = bound;
                }
            }

            return result.Any() ? result.ToArray() : null;
        }

        private static IEnumerable<string> SortStringsAsNumbers(IEnumerable<string> strings)
        {
            return strings
                .Where(b => !string.IsNullOrEmpty(b))
                .Select(b => decimal.Parse(b, NumberStyles.Float, CultureInfo.InvariantCulture))
                .OrderBy(b => b)
                .Distinct()
                .Select(b => b.ToString(CultureInfo.InvariantCulture));
        }
    }
}
