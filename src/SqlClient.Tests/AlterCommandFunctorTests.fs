module FSharp.Data.AlterCommandFunctorTests

open System.Data.SqlClient

open Xunit
open FsUnit.Xunit

[<Literal>]
let connection = ConnectionStrings.AdventureWorksLiteral

type GetString = SqlCommandProvider<"select 'azerty'", connection, SingleRow = true>

[<Fact>]
let ``alteration functor is called once and only once for AsSqlCommand``() =
    use conn = new SqlConnection(connection)
    conn.Open()
    let createCommand = conn.CreateCommand

    let calledOnce = ref false
    let alterCommand (c: SqlCommand) =
      Assert.False(!calledOnce)
      c.CommandText <- "foo"
      calledOnce := true
    let getString = new GetString(createCommand, Some alterCommand)
    let sqlCommand = getString.AsSqlCommand()
    Assert.True(sqlCommand.CommandText = "foo")
    Assert.True(!calledOnce)

[<Fact>]
let ``alteration functor is called once and only once for Execute``() =
    use conn = new SqlConnection(connection)
    conn.Open()
    let createCommand = conn.CreateCommand

    let calledOnce = ref false
    let alterCommand (c: SqlCommand) =
      Assert.False(!calledOnce)
      c.CommandText <- "select 'qwerty'"
      calledOnce := true
    (new GetString(createCommand, Some alterCommand)).Execute() |> should equal (Some "qwerty")
    Assert.True(!calledOnce)

[<Fact>]
let ``alteration functor is called once and only once for AsyncExecute``() =
    use conn = new SqlConnection(connection)
    conn.Open()
    let createCommand = conn.CreateCommand

    let calledOnce = ref false
    let alterCommand (c: SqlCommand) =
      Assert.False(!calledOnce)
      c.CommandText <- "select 'qwerty'"
      calledOnce := true
    (new GetString(createCommand, Some alterCommand)).AsyncExecute() |> Async.RunSynchronously |> should equal (Some "qwerty")
    Assert.True(!calledOnce)