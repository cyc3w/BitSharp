CREATE TABLE BlockchainMetadata
(
    Guid CHAR(16) CHARACTER SET OCTETS NOT NULL,
    RootBlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
    TotalWork CHAR(64) CHARACTER SET OCTETS NOT NULL,
    IsComplete INTEGER NOT NULL,
	CONSTRAINT PK_BlockchainMetaData PRIMARY KEY
	(
        Guid
	)
);

CREATE TABLE BlockMetadata
(
    Guid CHAR(16) CHARACTER SET OCTETS NOT NULL,
    RootBlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	BlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	PreviousBlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	"Work" CHAR(64) CHARACTER SET OCTETS NOT NULL,
	Height BIGINT NOT NULL,
	TotalWork CHAR(64) CHARACTER SET OCTETS NOT NULL,
	IsValid INTEGER,
	CONSTRAINT PK_BlockMetaData PRIMARY KEY
	(
        Guid,
        RootBlockHash,
		BlockHash
	)
);

CREATE INDEX IX_BlockMetadata_Guid_RootHash ON BlockMetadata ( Guid, RootBlockHash );

CREATE TABLE UtxoData
(
	Guid CHAR(16) CHARACTER SET OCTETS NOT NULL,
	RootBlockHash CHAR(32) CHARACTER SET OCTETS NOT NULL,
	UtxoChunkBytes BLOB SUB_TYPE BINARY NOT NULL
);

CREATE INDEX IX_UtxoData_Guid_RootBlockHash ON UtxoData ( Guid, RootBlockHash );
