(*** hide ***)
#r @"..\..\bin\net462\FSharp.Data.SqlClient.dll"
#r "System.Transactions"
open FSharp.Data
open System

[<Literal>]
let connectionString = @"Data Source=.;Initial Catalog=AdventureWorks2014;Integrated Security=True;TrustServerCertificate=true"

(**

Bulk Load
===================

*)
