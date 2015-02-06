module FSharp.Data.CreateCommandFunctorTests

open System.Data.SqlClient

open Xunit
open FsUnit.Xunit

open FSharp.Data.TypeProviderTest

[<Literal>]
let connection = ConnectionStrings.AdventureWorksLiteral

type GetDate = SqlCommandProvider<"select getdate()", connection>

[<Fact>]
let ``Setting the command timeout isn't overridden``() =
    use conn = new SqlConnection(connection)
    conn.Open()
    let createCommand() =
      let c = conn.CreateCommand()
      c.CommandTimeout <- 123
      c
    let getDate = new GetDate(createCommand)
    let sqlCommand = getDate.AsSqlCommand()
    Assert.True(sqlCommand.CommandTimeout = 123)
    
[<Fact>]
let asyncCustomRecord() =
    use conn = new SqlConnection(connection)
    conn.Open()
    let createCommand = conn.CreateCommand
    (new GetBitCoin(createCommand)).AsyncExecute("USD") |> Async.RunSynchronously |> Seq.length |> should equal 1

[<Fact>]
let singleRowOption() =
    use conn = new SqlConnection(connection)
    conn.Open()
    let createCommand = conn.CreateCommand
    (new NoneSingleton(createCommand)).Execute().IsNone |> should be True
    (new SomeSingleton(createCommand)).AsyncExecute() |> Async.RunSynchronously |> should equal (Some 1)
