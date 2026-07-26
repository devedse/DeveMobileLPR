-- Adapt table and column names to the schema produced by your RDW importer.
-- The application intentionally reads through this stable view contract.
CREATE VIEW rdw_vehicles AS
SELECT
    REPLACE(REPLACE(UPPER(kenteken), '-', ''), ' ', '') AS normalized_plate,
    merk AS make,
    handelsbenaming AS model,
    catalogusprijs AS catalog_price,
    CAST(SUBSTR(datum_eerste_toelating, 1, 4) AS INTEGER) AS registration_year,
    brandstof_omschrijving AS fuel_description,
    inrichting AS body_type
FROM rdw_raw;

-- Keep lookups O(log n), even with millions of RDW rows. SQLite can use this
-- expression index through the view because its expression is identical.
CREATE INDEX IF NOT EXISTS ix_rdw_raw_normalized_plate
ON rdw_raw(REPLACE(REPLACE(UPPER(kenteken), '-', ''), ' ', ''));
