using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentMigrator;
using Smartstore.Core.Catalog.Attributes;
using Smartstore.Core.Data.Migrations;

namespace Smartstore.Core.Migrations
{

    [MigrationVersion("2023-05-15 15:07:00", "Add Hash to AttributeSelection")]
    public class _20230515150700_AddAttributeSelectionHash : Migration
    {
        public override void Down()
        {

        }

        public override void Up()
        {
            var tableName = "ProductVariantAttributeCombination";
            if (Schema.Table(tableName).Exists())
            {
                Create.Column(nameof(ProductVariantAttributeCombination.HashValue))
                    .OnTable(tableName)
                    .AsInt32()
                    .NotNullable()
                    .WithDefaultValue(0);
                Create.Index("IX_ProductVariantAttributeCombination_HashValue")
                    .OnTable(tableName)
                    .OnColumn(nameof(ProductVariantAttributeCombination.HashValue));
                    
            }
        }
    }
}
